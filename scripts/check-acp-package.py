"""Verify the SDK's single-package distribution boundary."""
from pathlib import Path
import sys
from zipfile import ZipFile
import xml.etree.ElementTree as ET

packages = [p for p in Path(sys.argv[1]).glob('*.nupkg') if not p.name.endswith('.symbols.nupkg')]
assert len(packages) == 1, packages
with ZipFile(packages[0]) as archive:
    files = archive.namelist()
    assemblies = [p for p in files if p.endswith('.dll')]
    assert assemblies == ['lib/net10.0/OpenAgent.Runtime.Acp.dll'], assemblies
    assert 'README.md' in files
    assert not any('Tests' in p or 'TestConsole' in p or 'schema.json' in p or '/Generated/' in p for p in files)
    nuspec = ET.fromstring(archive.read(next(p for p in files if p.endswith('.nuspec'))))
    ns = {'n': nuspec.tag.split('}')[0][1:]}
    assert not nuspec.findall('.//n:dependency', ns), 'Runtime package dependency found'
    assert nuspec.find('.//n:license', ns).text == 'MIT'
    assert nuspec.find('.//n:repository', ns).get('url') == 'https://github.com/philfanzhou/OpenAgent'
print('PASS: one SDK DLL, no runtime dependencies or test/schema payload; README, license and repository metadata present')
