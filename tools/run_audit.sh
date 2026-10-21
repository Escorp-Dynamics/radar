#!/bin/sh
# Полный аудит движка по всем месяцам пакетного архива; сводка по месяцам — в stdout (TSV).
#   tools/run_audit.sh > artifacts/validation/engine-audit-months.tsv
# Переменные: RADAR_AUDIT_DIR — рабочий каталог (/tmp/months), RADAR_AUDIT_BIN — собранный Radar
# (по умолчанию собирается Release в /tmp/raudit: в ~1,7 раза быстрее Debug при том же выводе),
# RADAR_AUDIT_JOBS — параллельных месяцев (по числу ядер; месяц занимает ~250 МБ памяти).
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DIR=${RADAR_AUDIT_DIR:-/tmp/months}
JOBS=${RADAR_AUDIT_JOBS:-$(nproc 2>/dev/null || echo 4)}
if [ -z "$RADAR_AUDIT_BIN" ]; then
    dotnet build "$ROOT/Radar/Radar.csproj" -c Release -o /tmp/raudit >&2
    RADAR_AUDIT_BIN=/tmp/raudit/Radar
fi
python3 "$ROOT/tools/audit_export.py" "$DIR"
# зафиксированный снимок кеша геокодера: итоги не зависят от растущего кеша пользователя
export RADAR_GEOCODE_CACHE="$ROOT/artifacts/validation/geocode-audit.tsv"
export RADAR_AUDIT_BIN
cd "$DIR"
rm -f aud_*.tsv 20*.log
# упавший месяц не обрывает скрипт (set -e): сводка сама находит неполные логи и сообщает о них
ls 20*.json | sed 's/\.json$//' | sort -r | xargs -P"$JOBS" -I{} sh -c '"$RADAR_AUDIT_BIN" --engine-audit {}.json aud_{}.tsv > {}.log 2>&1' || true
python3 "$ROOT/tools/audit_summary.py" "$DIR"
