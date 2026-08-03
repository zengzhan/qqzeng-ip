const readline = require('readline');
const qzdb = require('./qzdb.js');

const dbPath = process.argv[2];
if (!dbPath) process.exit(1);

const searcher = new qzdb(dbPath);

const rl = readline.createInterface({
  input: process.stdin,
  output: process.stdout,
  terminal: false
});

rl.on('line', (line) => {
  const ip = line.trim();
  if (!ip) return;
  const res = searcher.findStr(ip);
  console.log(res !== null ? res : '');
});
