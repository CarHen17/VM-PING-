#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$project_root"
dotnet test modern/VmPing.Modern.slnx -c Release
for target in win-x64 linux-x64; do
    package_folder="$target"
    if [[ "$target" == "win-x64" ]]; then package_folder="windows-x64"; fi
    dotnet publish modern/src/VmPing.Desktop -c Release -r "$target" --self-contained true -p:PublishTrimmed=false -o "dist/$package_folder"
    mkdir -p "dist/$package_folder/notices"
    cp modern/notices/* "dist/$package_folder/notices/"
    cp LICENSE "dist/$package_folder/LICENSE.txt"
    cp modern/README.md "dist/$package_folder/README.md"
done
python3 - <<'PY'
from pathlib import Path
import shutil
root = Path('dist')
shutil.make_archive(str(root / 'vmPing-modern-windows-x64'), 'zip', root / 'windows-x64')
shutil.make_archive(str(root / 'vmPing-modern-linux-x64'), 'gztar', root / 'linux-x64')
PY
