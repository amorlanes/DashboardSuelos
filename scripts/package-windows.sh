#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_dir/dotnet-env.sh"
cd "$script_dir/.."
mkdir -p "$CONSTRUCTORA_TOOLS_ROOT/scratch"
package_work="$(mktemp -d "$CONSTRUCTORA_TOOLS_ROOT/scratch/constructora-portable.XXXXXX")"
export CONSTRUCTORA_PACKAGE_WORK="$package_work"
export CONSTRUCTORA_PACKAGE_SOURCE="$PWD"
export CONSTRUCTORA_PACKAGE_ZIP="${1:-$CONSTRUCTORA_TOOLS_ROOT/Constructora-Windows-portatil.zip}"
# RID-specific restores use an external lockfile; the development lockfiles are retained.
for component in wpf launcher; do
    case "$component" in
        wpf) project=src/Constructora.Wpf/Constructora.Wpf.csproj ;;
        launcher) project=tools/Constructora.Prueba/Constructora.Prueba.csproj ;;
    esac
    dotnet publish "$project" --configuration Release --runtime win-x64 \
        --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false \
        -p:RestorePackagesWithLockFile=false \
        "-p:NuGetLockFilePath=$package_work/$component-publish.lock.json" \
        --output "$package_work/$component"
done
python3 - <<'PY'
import hashlib
import os
import shutil
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

work = Path(os.environ['CONSTRUCTORA_PACKAGE_WORK'])
source = Path(os.environ['CONSTRUCTORA_PACKAGE_SOURCE'])
destination = work / 'Constructora-Windows'
destination.mkdir()
for component in ('wpf', 'launcher'):
    component_root = destination if component == 'wpf' else destination / 'Preparador'
    for path in (work / component).rglob('*'):
        if not path.is_file():
            continue
        target = component_root / path.relative_to(work / component)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists() and target.read_bytes() != path.read_bytes():
            raise SystemExit(f'Dependencia incompatible entre publicaciones: {target.name}')
        shutil.copy2(path, target)
for name in ('Probar.cmd', 'LEEME.txt'):
    shutil.copy2(source / 'tools' / 'Constructora.Prueba' / name, destination / name)
required = ('Constructora.Wpf.exe', 'hostfxr.dll', 'hostpolicy.dll',
            'coreclr.dll', 'PresentationFramework.dll', 'e_sqlite3.dll',
            'Preparador/Constructora.Prueba.exe', 'Preparador/hostfxr.dll',
            'Preparador/hostpolicy.dll', 'Preparador/coreclr.dll', 'Preparador/e_sqlite3.dll')
for name in required:
    if not (destination / name).is_file():
        raise SystemExit(f'Falta un componente del paquete: {name}')
files = sorted(path for path in destination.rglob('*') if path.is_file())
manifest = '\n'.join(f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(destination).as_posix()}'
                     for path in files) + '\n'
(destination / 'SHA256SUMS.txt').write_text(manifest, encoding='utf-8')
archive = Path(os.environ['CONSTRUCTORA_PACKAGE_ZIP'])
archive.parent.mkdir(parents=True, exist_ok=True)
with ZipFile(archive, 'w', ZIP_DEFLATED) as output:
    for path in sorted(destination.rglob('*')):
        if path.is_file():
            output.write(path, Path(destination.name) / path.relative_to(destination))
with ZipFile(archive) as output:
    if output.testzip() is not None:
        raise SystemExit('El archivo ZIP no ha superado su comprobación de integridad.')
print(f'Paquete Windows x64: {archive}')
print(f'Tamaño: {archive.stat().st_size / (1024 * 1024):.1f} MiB')
print(f'SHA256: {hashlib.sha256(archive.read_bytes()).hexdigest()}')
print(f'Carpeta de comprobación: {destination}')
PY
