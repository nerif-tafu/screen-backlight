Import("env")

from configparser import ConfigParser
from pathlib import Path

secrets_path = Path(env.subst("$PROJECT_DIR")) / "wifi_secrets.ini"

if not secrets_path.is_file():
    Return()

config = ConfigParser()
config.read(secrets_path, encoding="utf-8")

if not config.has_section("wifi"):
    print("Warning: wifi_secrets.ini has no [wifi] section")
    Return()

ssid = config.get("wifi", "ssid", fallback="").strip()
password = config.get("wifi", "password", fallback="").strip()

if ssid:
    env.Append(BUILD_FLAGS=[f'-DWIFI_SSID=\\"{ssid}\\"'])
if password:
    env.Append(BUILD_FLAGS=[f'-DWIFI_PASSWORD=\\"{password}\\"'])
