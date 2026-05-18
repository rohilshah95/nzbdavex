import styles from "./records-block.module.css";
import { formatBytes } from "../../utils/format";

export type RecordsBlockProps = {
    records: {
        bestDayBytes: number,
        bestDayAt: number | null,
        bestHourBytes: number,
        bestHourAt: number | null,
    },
};

export function RecordsBlock({ records }: RecordsBlockProps) {
    const isEmpty = records.bestDayBytes === 0 && records.bestHourBytes === 0;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Records</h3>
                <div className={styles.sub}>Personal bests since you started</div>
            </div>

            {isEmpty ? (
                <div className={styles.empty}>Records appear after some activity.</div>
            ) : (
                <div className={styles.grid}>
                    <Record
                        label="Busiest day"
                        value={formatBytes(records.bestDayBytes)}
                        when={records.bestDayAt ? formatDay(records.bestDayAt) : ""}
                    />
                    <Record
                        label="Busiest hour"
                        value={formatBytes(records.bestHourBytes)}
                        when={records.bestHourAt ? formatHour(records.bestHourAt) : ""}
                    />
                </div>
            )}
        </div>
    );
}

function Record({ label, value, when }: { label: string, value: string, when: string }) {
    return (
        <div className={styles.cell}>
            <div className={styles.label}>{label}</div>
            <div className={styles.value}>{value}</div>
            {when && <div className={styles.when}>{when}</div>}
        </div>
    );
}

function formatDay(ms: number): string {
    const d = new Date(ms);
    return d.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

function formatHour(ms: number): string {
    const d = new Date(ms);
    const day = d.toLocaleDateString(undefined, { day: "numeric", month: "short" });
    const hh = String(d.getHours()).padStart(2, "0");
    return `${day} · ${hh}:00`;
}
