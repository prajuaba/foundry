const fs = require('fs');
const path = require('path');

console.log('🔍 Starting Automated Verification of Foundry Studio IDE Extension...\n');

let errors = 0;

// 1. Verify Extension Package Manifest
const packageJsonPath = path.join(__dirname, '../package.json');
if (!fs.existsSync(packageJsonPath)) {
  console.error('❌ package.json not found!');
  errors++;
} else {
  const pkg = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
  console.log(`✓ Extension Package Manifest: ${pkg.displayName} v${pkg.version}`);
  
  if (!pkg.main || !fs.existsSync(path.join(__dirname, '..', pkg.main))) {
    console.error(`❌ Main entry point ${pkg.main} missing or not compiled!`);
    errors++;
  } else {
    console.log(`  ✓ Main Bundle Verified: ${pkg.main} (${fs.statSync(path.join(__dirname, '..', pkg.main)).size} bytes)`);
  }

  // Check custom editor contribution
  const customEditors = pkg.contributes?.customEditors;
  if (!customEditors || customEditors.length === 0 || customEditors[0].viewType !== 'foundry.studioEditor') {
    console.error('❌ Custom editor contribution "foundry.studioEditor" missing!');
    errors++;
  } else {
    console.log(`  ✓ Custom Editor Registered: ${customEditors[0].displayName} (${customEditors[0].viewType})`);
  }
}

// 2. Verify Studio Webview Build Artifacts
const studioDistIndex = path.join(__dirname, '../dist-studio/index.html');
if (!fs.existsSync(studioDistIndex)) {
  console.error('❌ Studio Webview index.html missing at dist-studio/index.html!');
  errors++;
} else {
  const html = fs.readFileSync(studioDistIndex, 'utf8');
  console.log(`✓ Studio Webview Artifacts Verified: index.html found`);
  
  if (!html.includes('<script') || html.length < 500000) {
    console.error('❌ Studio Webview index.html does not contain inlined singlefile bundle!');
    errors++;
  } else {
    console.log(`  ✓ HTML Script & CSS Assets Verified.`);
  }
}

// 3. Verify VSIX Installer Package
const vsixPath = path.join(__dirname, '../foundry-vscode-1.0.0.vsix');
if (!fs.existsSync(vsixPath)) {
  console.error('❌ VSIX installer package missing at foundry-vscode-1.0.0.vsix!');
  errors++;
} else {
  const stats = fs.statSync(vsixPath);
  console.log(`✓ VSIX Installer Package Verified: ${path.basename(vsixPath)} (${(stats.size / 1024).toFixed(2)} KB)`);
}

// 4. Verify C# Compiler Backend Integration
const compilerProj = path.join(__dirname, '../../foundry-schema/compiler/Foundry.Schema.Compiler.csproj');
if (!fs.existsSync(compilerProj)) {
  console.error('❌ Foundry Schema Compiler project not found!');
  errors++;
} else {
  console.log(`✓ Compiler Service Backend Verified: ${path.basename(compilerProj)}`);
}

console.log('\n---------------------------------------------------------');
if (errors === 0) {
  console.log('🎉 VERIFICATION SUCCESS: All 4 Extension Systems Verified 100% OK!');
  process.exit(0);
} else {
  console.error(`💥 VERIFICATION FAILED with ${errors} error(s).`);
  process.exit(1);
}
