#!/usr/bin/env python3
"""
Манифест автообновления релиза: update.json (версия, SHA-256 и размер каждого файла) и подпись update.json.sig
(ECDSA P-256 / SHA-256, DER) закрытым ключом из переменной UPDATE_SIGNING_KEY (PEM). Приложение проверяет подпись
открытым ключом Radar.Core/Services/Updater.cs (PublicKey) и ставит файл, только если совпали хеш и размер.

    UPDATE_SIGNING_KEY="$(cat key.pem)" sign_update.py <версия> <каталог с файлами релиза> [имя манифеста]

Имя по умолчанию — update.json; macOS-сборки, которые дописываются в релиз позже, получают свой update-osx.json:
общий манифест не перезаписывается, пока клиенты его читают.
"""
import hashlib, json, os, subprocess, sys, tempfile

version, dist = sys.argv[1], sys.argv[2]
name = sys.argv[3] if len(sys.argv) > 3 else "update.json"
key = os.environ.get("UPDATE_SIGNING_KEY", "")
if not key.strip():
    # без ключа релиз всё равно выходит: приложение не найдёт манифест и предложит скачать вручную
    print("::warning::UPDATE_SIGNING_KEY не задан — релиз без манифеста автообновления")
    sys.exit(0)

assets = {}
for f in sorted(os.listdir(dist)):
    if f.startswith("update"):
        continue
    path = os.path.join(dist, f)
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    assets[f] = {"name": f, "sha256": h.hexdigest(), "size": os.path.getsize(path)}

manifest = os.path.join(dist, name)
with open(manifest, "w", encoding="utf-8") as fh:
    json.dump({"version": version, "assets": [assets[n] for n in sorted(assets)]}, fh, ensure_ascii=False, indent=1)

with tempfile.NamedTemporaryFile("w", suffix=".pem", delete=False) as k:
    k.write(key)
try:
    subprocess.run(["openssl", "dgst", "-sha256", "-sign", k.name, "-out", manifest + ".sig", manifest], check=True)
finally:
    os.unlink(k.name)
print(f"{name}: {len(assets)} файлов, подпись {os.path.getsize(manifest + '.sig')} байт")
