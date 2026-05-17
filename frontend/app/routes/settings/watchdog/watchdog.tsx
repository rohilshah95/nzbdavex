import { Form, Button } from "react-bootstrap";
import { type Dispatch, type SetStateAction, useCallback, useEffect, useState } from "react";
import styles from "./watchdog.module.css";
import type { PlaybackAttempt, PlaybackAttemptOutcome } from "~/clients/backend-client.server";

type WatchdogSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WatchdogSettings({ config, setNewConfig }: WatchdogSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const verifyMode = config["play.verify-mode"] ?? "none";
    const enabled = (config["play.watchdog-enabled"] ?? "true") === "true";

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionTitle}>Playback fast-fail</div>
                <div className={styles.sectionDescription}>
                    When a user clicks Play, nzbdav tries the top-ranked release first; if it can't deliver
                    fast enough, alternatives are tried automatically. These knobs control how aggressive
                    that fallback is, so the player never hangs on a dead release.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Check
                    type="switch"
                    id="play-watchdog-enabled"
                    label="Enable playback watchdog"
                    checked={enabled}
                    onChange={e => set("play.watchdog-enabled", String(e.target.checked))} />
                <p className={styles.hint}>
                    When off, a Play click just processes the single chosen release (legacy behavior).
                    When on, the watchdog tries alternative releases on failure and dedupes in-flight queue items.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Total budget (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={3}
                    max={180}
                    value={config["play.total-budget-seconds"] ?? "30"}
                    onChange={e => set("play.total-budget-seconds", e.target.value)} />
                <p className={styles.hint}>
                    Hard ceiling for a Play click. Big UHD releases need ~15–30s for the queue to extract
                    file metadata. If exceeded, the player gets a retry-able error; the queue item keeps
                    processing in the background and a re-click resolves it. Default 30.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Hedge delay (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={30}
                    disabled={!enabled}
                    value={config["play.hedge-delay-seconds"] ?? "3"}
                    onChange={e => set("play.hedge-delay-seconds", e.target.value)} />
                <p className={styles.hint}>
                    If the primary candidate hasn't passed verification by this many seconds, backup
                    candidates start in parallel. Lower = more eager fallback, slightly higher provider load. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Max candidates per click</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={10}
                    disabled={!enabled}
                    value={config["play.max-candidates"] ?? "3"}
                    onChange={e => set("play.max-candidates", e.target.value)} />
                <p className={styles.hint}>
                    How many alternative releases to try per click before giving up. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Verify mode</Form.Label>
                <Form.Select
                    className={styles.input}
                    disabled={!enabled}
                    value={verifyMode}
                    onChange={e => set("play.verify-mode", e.target.value)}>
                    <option value="none">none — no pre-check, enqueue right away (recommended)</option>
                    <option value="stat">stat — STAT first segment (~0.2s; skips candidates flagged dead)</option>
                    <option value="body">body — strict, downloads first article (~1–2s)</option>
                </Form.Select>
                <p className={styles.hint}>
                    `none` is safest: every candidate gets enqueued and falls back to the next on confirmed
                    failure. `stat` adds a pre-filter but can drop legit releases when providers are slow.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Negative-cache TTL (minutes)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={1440}
                    disabled={!enabled}
                    value={config["play.candidate-negative-cache-minutes"] ?? "5"}
                    onChange={e => set("play.candidate-negative-cache-minutes", e.target.value)} />
                <p className={styles.hint}>
                    How long a recently-failed release is skipped on subsequent clicks, so we don't hammer
                    the same dead release. Default 5.
                </p>
            </Form.Group>

            <hr />
            <RecentAttemptsSection />
        </div>
    );
}

function RecentAttemptsSection() {
    const [attempts, setAttempts] = useState<PlaybackAttempt[]>([]);
    const [error, setError] = useState<string | null>(null);
    const [loading, setLoading] = useState(false);

    const refresh = useCallback(async () => {
        setLoading(true);
        setError(null);
        try {
            const r = await fetch("/settings/watchdog-attempts?limit=200");
            if (!r.ok) throw new Error(`HTTP ${r.status}`);
            const data = await r.json();
            setAttempts(data.attempts ?? []);
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => { refresh(); }, [refresh]);

    // group by clickId, sorted by latest attempt first
    const groups = groupByClick(attempts);

    return (
        <div className={styles.section}>
            <div className={styles.attemptsHeader}>
                <div className={styles.sectionTitle}>Recent attempts</div>
                <Button variant="secondary" size="sm" onClick={refresh} disabled={loading}>
                    {loading ? "Loading…" : "Refresh"}
                </Button>
            </div>
            <div className={styles.sectionDescription}>
                Every release the watchdog tried, including the ones that never made it to the queue
                (pre-verify rejects). Grouped by Play click. Persists until app restart.
            </div>

            {error && <div className={styles.errorBox}>Could not load: {error}</div>}
            {!error && groups.length === 0 && (
                <div className={styles.sectionDescription}>No attempts recorded yet.</div>
            )}

            {groups.map(g => (
                <div key={g.clickId} className={styles.clickGroup}>
                    <div className={styles.clickHeader}>
                        <span className={styles.clickWhen}>{formatAge(g.firstAt)}</span>
                        <span className={styles.clickTitle}>{g.requestedTitle}</span>
                        <span className={styles.clickMeta}>{g.contentType}</span>
                        {g.hasWinner ? (
                            <span className={`${styles.outcomeBadge} ${styles.outcomeWin}`}>✓ resolved</span>
                        ) : (
                            <span className={`${styles.outcomeBadge} ${styles.outcomeNoWin}`}>✗ no candidate worked</span>
                        )}
                    </div>
                    <table className={styles.attemptTable}>
                        <thead>
                            <tr>
                                <th>#</th>
                                <th>Candidate</th>
                                <th>Indexer</th>
                                <th>Size</th>
                                <th>Outcome</th>
                                <th>Reason</th>
                                <th>Took</th>
                            </tr>
                        </thead>
                        <tbody>
                            {g.attempts.map((a, i) => (
                                <tr key={i} className={a.isWinner ? styles.winnerRow : undefined}>
                                    <td>{a.rankIndex + 1}</td>
                                    <td className={styles.titleCell} title={a.candidateTitle}>{a.candidateTitle}</td>
                                    <td>{a.indexerName}</td>
                                    <td>{formatBytes(a.size)}</td>
                                    <td><OutcomeBadge outcome={a.outcome} winner={a.isWinner} /></td>
                                    <td className={styles.reasonCell}>{a.failReason ?? "—"}</td>
                                    <td>{a.durationMs}ms</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            ))}
        </div>
    );
}

function OutcomeBadge({ outcome, winner }: { outcome: PlaybackAttemptOutcome, winner: boolean }) {
    if (winner) return <span className={`${styles.outcomeBadge} ${styles.outcomeWin}`}>winner</span>;
    const cls =
        outcome === "QueueCompleted" ? styles.outcomeOk
        : outcome === "PreVerifyAvailable" ? styles.outcomeOk
        : outcome === "BudgetTimeout" ? styles.outcomeWarn
        : outcome === "Cancelled" ? styles.outcomeWarn
        : styles.outcomeBad;
    return <span className={`${styles.outcomeBadge} ${cls}`}>{shortOutcome(outcome)}</span>;
}

function shortOutcome(o: PlaybackAttemptOutcome): string {
    switch (o) {
        case "QueueCompleted": return "completed";
        case "QueueFailed": return "queue failed";
        case "EnqueueFailed": return "enqueue failed";
        case "PreVerifyDead": return "verify: dead";
        case "PreVerifyTimeout": return "verify: timeout";
        case "PreVerifyAvailable": return "verify: ok";
        case "BudgetTimeout": return "budget timeout";
        case "Cancelled": return "cancelled";
        default: return o;
    }
}

type ClickGroup = {
    clickId: string,
    firstAt: number,
    requestedTitle: string,
    contentType: string,
    hasWinner: boolean,
    attempts: PlaybackAttempt[],
};

function groupByClick(list: PlaybackAttempt[]): ClickGroup[] {
    const map = new Map<string, ClickGroup>();
    for (const a of list) {
        const g = map.get(a.clickId);
        if (g) {
            g.attempts.push(a);
            if (a.attemptedAtUnix > g.firstAt) g.firstAt = a.attemptedAtUnix;
            if (a.isWinner) g.hasWinner = true;
        } else {
            map.set(a.clickId, {
                clickId: a.clickId,
                firstAt: a.attemptedAtUnix,
                requestedTitle: a.requestedTitle,
                contentType: a.contentType,
                hasWinner: a.isWinner,
                attempts: [a],
            });
        }
    }
    const arr = Array.from(map.values());
    arr.sort((x, y) => y.firstAt - x.firstAt);
    for (const g of arr) g.attempts.sort((x, y) => x.rankIndex - y.rankIndex);
    return arr;
}

function formatBytes(bytes: number): string {
    if (bytes <= 0) return "—";
    const u = ["B", "KB", "MB", "GB", "TB"];
    let i = 0;
    let v = bytes;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2)} ${u[i]}`;
}

function formatAge(unixSeconds: number): string {
    const age = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
    return `${Math.floor(age / 86400)}d ago`;
}

export function isWatchdogSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["play.watchdog-enabled"] !== newConfig["play.watchdog-enabled"]
        || config["play.total-budget-seconds"] !== newConfig["play.total-budget-seconds"]
        || config["play.hedge-delay-seconds"] !== newConfig["play.hedge-delay-seconds"]
        || config["play.max-candidates"] !== newConfig["play.max-candidates"]
        || config["play.verify-mode"] !== newConfig["play.verify-mode"]
        || config["play.candidate-negative-cache-minutes"] !== newConfig["play.candidate-negative-cache-minutes"];
}
