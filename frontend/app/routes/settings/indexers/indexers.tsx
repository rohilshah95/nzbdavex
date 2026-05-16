import { Button, Form, Card, InputGroup, Spinner } from "react-bootstrap";
import styles from "./indexers.module.css";
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect } from "react";

type IndexersSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

interface ConnectionDetails {
    Name: string;
    Url: string;
    ApiKey: string;
    Enabled: boolean;
    UserAgent?: string;
    MaxRequestsPerMinute?: number;
}

interface IndexerConfig {
    Indexers: ConnectionDetails[];
}

function parseConfig(raw: string): IndexerConfig {
    try {
        const parsed = JSON.parse(raw || "{}");
        return { Indexers: parsed.Indexers ?? [] };
    } catch {
        return { Indexers: [] };
    }
}

export function IndexersSettings({ config, setNewConfig }: IndexersSettingsProps) {
    const indexerConfig = parseConfig(config["indexers.instances"]);

    const update = useCallback((next: IndexerConfig) => {
        setNewConfig({ ...config, "indexers.instances": JSON.stringify(next) });
    }, [config, setNewConfig]);

    const add = useCallback(() => {
        update({
            Indexers: [
                ...indexerConfig.Indexers,
                { Name: "", Url: "", ApiKey: "", Enabled: true, UserAgent: "", MaxRequestsPerMinute: 0 }
            ]
        });
    }, [indexerConfig, update]);

    const remove = useCallback((index: number) => {
        update({ Indexers: indexerConfig.Indexers.filter((_, i) => i !== index) });
    }, [indexerConfig, update]);

    const change = useCallback((index: number, field: keyof ConnectionDetails, value: string | boolean | number) => {
        update({
            Indexers: indexerConfig.Indexers.map((x, i) =>
                i === index ? { ...x, [field]: value } : x
            )
        });
    }, [indexerConfig, update]);

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Newznab Indexers</div>
                    <Button variant="primary" size="sm" onClick={add}>Add</Button>
                </div>
                {indexerConfig.Indexers.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No indexers configured. Add a Newznab-compatible indexer (e.g. NZBgeek, NZBHydra2, Prowlarr) to enable search.
                    </p>
                ) : (
                    indexerConfig.Indexers.map((instance, index) => (
                        <IndexerForm
                            key={index}
                            instance={instance}
                            index={index}
                            onChange={change}
                            onRemove={remove}
                        />
                    ))
                )}
            </div>
        </div>
    );
}

interface IndexerFormProps {
    instance: ConnectionDetails;
    index: number;
    onChange: (index: number, field: keyof ConnectionDetails, value: string | boolean | number) => void;
    onRemove: (index: number) => void;
}

function IndexerForm({ instance, index, onChange, onRemove }: IndexerFormProps) {
    const [state, setState] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');

    useEffect(() => { setState('idle'); }, [instance.Url, instance.ApiKey]);

    const test = useCallback(async () => {
        if (!instance.Url.trim() || !instance.ApiKey.trim()) return;
        setState('testing');
        try {
            const fd = new FormData();
            fd.append('url', instance.Url);
            fd.append('apiKey', instance.ApiKey);
            if (instance.UserAgent?.trim()) fd.append('userAgent', instance.UserAgent);
            const r = await fetch('/api/test-indexer-connection', { method: 'POST', body: fd });
            const data = await r.json();
            setState(data.status && data.connected ? 'success' : 'error');
        } catch {
            setState('error');
        }
    }, [instance.Url, instance.ApiKey, instance.UserAgent]);

    return (
        <Card className={styles.instanceCard}>
            <button className={styles.closeButton} onClick={() => onRemove(index)} aria-label="Remove">×</button>
            <Card.Body>
                <Form.Group>
                    <Form.Label>Name</Form.Label>
                    <Form.Control
                        type="text"
                        className={styles.input}
                        placeholder="e.g. NZBgeek"
                        value={instance.Name}
                        onChange={e => onChange(index, 'Name', e.target.value)} />
                </Form.Group>
                <Form.Group>
                    <Form.Label>URL</Form.Label>
                    <InputGroup className={styles.input}>
                        <Form.Control
                            type="text"
                            placeholder="https://api.nzbgeek.info"
                            value={instance.Url}
                            onChange={e => onChange(index, 'Url', e.target.value)} />
                        {instance.Url.trim() && instance.ApiKey.trim() && (
                            <Button
                                variant={state === 'success' ? 'success' : state === 'error' ? 'danger' : 'secondary'}
                                onClick={test}
                                disabled={state === 'testing'}
                                className={styles.testButton}
                            >
                                {state === 'testing' ? <Spinner animation="border" size="sm" />
                                    : state === 'success' ? '✓'
                                    : state === 'error' ? '✗'
                                    : 'Test'}
                            </Button>
                        )}
                    </InputGroup>
                </Form.Group>
                <Form.Group>
                    <Form.Label>API Key</Form.Label>
                    <Form.Control
                        type="password"
                        className={styles.input}
                        value={instance.ApiKey}
                        onChange={e => onChange(index, 'ApiKey', e.target.value)} />
                </Form.Group>
                <Form.Group>
                    <Form.Label>User-Agent <span style={{ opacity: 0.6, fontWeight: 'normal' }}>(optional)</span></Form.Label>
                    <Form.Control
                        type="text"
                        className={styles.input}
                        placeholder="Leave blank to use global default"
                        value={instance.UserAgent ?? ""}
                        onChange={e => onChange(index, 'UserAgent', e.target.value)} />
                </Form.Group>
                <Form.Group>
                    <Form.Label>Max requests / minute <span style={{ opacity: 0.6, fontWeight: 'normal' }}>(0 = unlimited)</span></Form.Label>
                    <Form.Control
                        type="number"
                        min={0}
                        className={styles.input}
                        placeholder="0"
                        value={instance.MaxRequestsPerMinute ?? 0}
                        onChange={e => onChange(index, 'MaxRequestsPerMinute', parseInt(e.target.value || "0", 10))} />
                </Form.Group>
                <Form.Check
                    type="switch"
                    label="Enabled"
                    checked={instance.Enabled}
                    onChange={e => onChange(index, 'Enabled', e.target.checked)} />
            </Card.Body>
        </Card>
    );
}

export function isIndexersSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["indexers.instances"] !== newConfig["indexers.instances"];
}

export function isIndexersSettingsValid(newConfig: Record<string, string>) {
    try {
        const c = parseConfig(newConfig["indexers.instances"]);
        for (const i of c.Indexers) {
            if (!i.Name.trim()) return false;
            if (!i.ApiKey.trim()) return false;
            try { new URL(i.Url); } catch { return false; }
        }
        return true;
    } catch {
        return false;
    }
}
