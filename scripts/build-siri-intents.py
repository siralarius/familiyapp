#!/usr/bin/env python3
"""Compile the small Swift App Intents bridge and Apple's discovery metadata."""
import json
import pathlib
import subprocess
import sys

rid, destination = sys.argv[1:]
out = pathlib.Path(destination).resolve()
out.mkdir(parents=True, exist_ok=True)
source = pathlib.Path(__file__).resolve().parents[1] / 'src/FamilyApp.Maui/Platforms/iOS/Siri/ShoppingIntents.swift'
platform, architecture = {
    'ios-arm64': ('iphoneos', 'arm64'),
    'iossimulator-arm64': ('iphonesimulator', 'arm64'),
    'iossimulator-x64': ('iphonesimulator', 'x86_64'),
}[rid]

def output(*args):
    return subprocess.check_output(args, text=True).strip()

def run(*args):
    subprocess.run(args, check=True)

sdk = output('xcrun', '--sdk', platform, '--show-sdk-path')
triple = architecture + '-apple-ios15.0' + ('-simulator' if platform == 'iphonesimulator' else '')
protocols = out / 'protocols.json'
protocols.write_text(json.dumps(['AppIntent', 'AppShortcutsProvider', 'AppEntity', 'AppEnum', 'EntityQuery', 'AppIntentsPackage']))
constants = out / 'ShoppingIntents.swiftconstvalues'
object_file = out / 'ShoppingIntents.o'
library = out / 'libFamilyShoppingIntents.a'
run('xcrun', 'swiftc', '-parse-as-library', '-emit-object', '-module-name', 'FamilyShoppingIntents',
    '-target', triple, '-sdk', sdk, '-emit-const-values', '-emit-const-values-path', str(constants),
    '-Xfrontend', '-const-gather-protocols-file', '-Xfrontend', str(protocols), str(source), '-o', str(object_file))
run('xcrun', 'libtool', '-static', '-o', str(library), str(object_file))
source_list = out / 'sources.txt'
source_list.write_text(str(source) + '\n')
constant_list = out / 'constants.txt'
constant_list.write_text(str(constants) + '\n')
developer = pathlib.Path(output('xcode-select', '-p'))
# xcrun respects DEVELOPER_DIR, as used by the iOS CI workflow.
import os
developer = pathlib.Path(os.environ.get('DEVELOPER_DIR', str(developer)))
run('xcrun', 'appintentsmetadataprocessor', '--output', str(out),
    '--toolchain-dir', str(developer / 'Toolchains/XcodeDefault.xctoolchain'),
    '--module-name', 'FamilyShoppingIntents', '--sdk-root', sdk,
    '--xcode-version', output('xcodebuild', '-version').split()[-1],
    '--platform-family', 'iOS', '--deployment-target', '15.0', '--target-triple', triple,
    '--source-file-list', str(source_list), '--swift-const-vals-list', str(constant_list),
    '--binary-file', str(library))
metadata = out / 'Metadata.appintents/extract.actionsdata'
if 'AddFamilyShopping' not in json.loads(metadata.read_text()).get('actions', {}):
    raise RuntimeError('Shopping App Intent missing from generated metadata')
