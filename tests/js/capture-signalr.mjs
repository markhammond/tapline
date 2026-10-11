// Captures what the SignalR JS client's MessagePack protocol writes (the control invocations tapline.js sends over a circuit)
// and how it reads a frame envelope the C# library wrote — into fixtures/signalr.json, which the tests compare against
// without the client. Not run by the tests, and nothing in the repository installs anything for it. To capture again
// (another client version, or the envelope changed), in a scratch directory outside the repository:
//
//   npm install @microsoft/signalr@10 @microsoft/signalr-protocol-msgpack@10
//   cp <repo>/tests/js/capture-signalr.mjs . && node capture-signalr.mjs <repo>
//
// (run the C# tests first: the envelope is the one fixtures/frames.json holds.)
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
import { MessageType, NullLogger } from '@microsoft/signalr';

const repo = process.argv[2] ?? join(import.meta.dirname, '..', '..');
const fixtures = join(repo, 'tests', 'js', 'fixtures');
const require = createRequire(import.meta.url);
const version = require('@microsoft/signalr-protocol-msgpack/package.json').version;
const protocol = new MessagePackHubProtocol();
const hex = bytes => Buffer.from(new Uint8Array(bytes)).toString('hex').toUpperCase();

// Every control invocation, with arguments spanning each MessagePack integer width the client may choose.
const widths = [0, 1, 127, 128, 255, 256, 65535, 65536, 4294967295, 4294967296, 9007199254740991];
const sends = [];
const send = (target, ...args) => sends.push({ target, args, hex: hex(protocol.writeMessage({ type: MessageType.Invocation, target, arguments: args })) });
for (const w of widths) {
  send('Tap.Ack', 3, 77, w, w);
  send('Tap.Resume', 3, 77, w, w);
}
send('Tap.Activate', 3, 77, 7);
send('Tap.Activate', 65535, 4294967295, 4294967295);
send('Tap.Resync', 3, 77, 7);
send('Tap.Close', 3, 77, 7, 0);
send('Tap.Close', 3, 77, 7, 11);
send('Tap.Fetched', 3, 77, 0, 7, 4294967295);

// The C# envelope, read by the client: Tap.Frame(seq, payload).
const frames = JSON.parse(readFileSync(join(fixtures, 'frames.json')));
const envelope = Buffer.from(frames.envelopeHex, 'hex');
const [message] = protocol.parseMessages(envelope.buffer.slice(envelope.byteOffset, envelope.byteOffset + envelope.length), NullLogger.instance);
const read = { type: message.type, target: message.target, seq: message.arguments[0], payloadIsBytes: message.arguments[1] instanceof Uint8Array, payloadHex: hex(message.arguments[1]) };

writeFileSync(join(fixtures, 'signalr.json'), JSON.stringify({ client: `@microsoft/signalr-protocol-msgpack ${version}`, sends, envelope: { hex: frames.envelopeHex, read } }, null, 1) + '\n');
console.log(`captured ${sends.length} sends and the envelope (client ${version})`);
