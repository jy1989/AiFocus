// Optional local bridge smoke test. Requires Node.js and a running AI Cockpit.
const { spawn } = require("node:child_process");
const path = require("node:path");
const host = path.resolve(__dirname, "../src/AiCockpit.Bridge/bin/Debug/net9.0/AiCockpit.Bridge.exe");
const child = spawn(host, [], { stdio: ["pipe", "pipe", "pipe"] });
let errors = "";
child.stderr.on("data", chunk => errors += chunk.toString());
const payload = Buffer.from(JSON.stringify({
  type: "snapshot", profileId: "smoke-test-" + process.pid,
  browser: "Chrome", tabs: []
}), "utf8");
const header = Buffer.alloc(4);
header.writeUInt32LE(payload.length, 0);
child.stdin.write(Buffer.concat([header, payload]));
setTimeout(() => {
  const alive = child.exitCode === null;
  child.kill();
  console.log(alive ? "BRIDGE_CONNECTION_OK" : "BRIDGE_FAILED: " + errors.trim());
  process.exitCode = alive ? 0 : 1;
}, 5500);
