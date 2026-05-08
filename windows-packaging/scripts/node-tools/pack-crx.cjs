'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const CRX = require('crx');

function extensionIdFromPrivateKeyPem(pem) {
  const pubDer = crypto.createPublicKey(pem).export({ type: 'spki', format: 'der' });
  const hash = crypto.createHash('sha256').update(pubDer).digest();
  let id = '';
  for (let i = 0; i < 16; i++) {
    id += String.fromCharCode(97 + (hash[i] & 0x0f));
    id += String.fromCharCode(97 + ((hash[i] >> 4) & 0x0f));
  }
  return id;
}

async function main() {
  const extDir = process.argv[2];
  const keyPath = process.argv[3];
  const outCrx = process.argv[4];
  const outIdPath = process.argv[5];

  if (!extDir || !keyPath || !outCrx || !outIdPath) {
    console.error('用法: node pack-crx.cjs <extensionDir> <key.pem> <out.crx> <extension-id.txt>');
    process.exit(1);
  }

  if (!fs.existsSync(keyPath)) {
    console.error('缺少私钥: ' + keyPath);
    process.exit(1);
  }

  const privateKey = fs.readFileSync(keyPath);
  const id = extensionIdFromPrivateKeyPem(privateKey);
  fs.mkdirSync(path.dirname(outCrx), { recursive: true });
  fs.mkdirSync(path.dirname(outIdPath), { recursive: true });

  const crx = new CRX({ privateKey });
  await crx.load(path.resolve(extDir));
  const buffer = await crx.pack();
  fs.writeFileSync(outCrx, buffer);
  fs.writeFileSync(outIdPath, id + '\n', 'utf8');
  console.log('CRX -> ' + outCrx);
  console.log('ExtensionId -> ' + id);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
