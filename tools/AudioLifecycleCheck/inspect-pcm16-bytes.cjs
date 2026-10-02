// Offline diagnostic only. Does not rewrite WAVs or feed corrected data to a verdict.
// Usage: node inspect-pcm16-bytes.cjs float32-stereo.wav channel1Based endpointVolumeDb
const fs = require('fs');
const [filename, channelText = '2', volumeText = '-10.508596'] = process.argv.slice(2);
const b = fs.readFileSync(filename);
let p = 12, data, channels, bits;
while (p + 8 <= b.length) {
  const name = b.toString('ascii', p, p + 4), n = b.readUInt32LE(p + 4);
  if (name === 'fmt ') { channels = b.readUInt16LE(p + 10); bits = b.readUInt16LE(p + 22); }
  if (name === 'data') data = b.subarray(p + 8, p + 8 + n);
  p += 8 + n + n % 2;
}
if (!data || bits !== 32) throw Error('Expected probe float32 WAV');
const channel = Number(channelText) - 1, gain = 10 ** (Number(volumeText) / 20);
if (channel < 0 || channel >= channels) throw Error('Channel outside WAV');
let sum = 0, swappedSum = 0, count = 0, closeToInteger = 0;
const codes = new Map();
for (let i = channel * 4; i + 4 <= data.length; i += channels * 4) {
  const sample = data.readFloatLE(i), raw = sample / gain * 32768, code = Math.round(raw);
  if (Math.abs(raw - code) < .01) closeToInteger++;
  const word = code & 65535, swappedWord = ((word & 255) << 8) | (word >>> 8);
  const signed = swappedWord >= 32768 ? swappedWord - 65536 : swappedWord;
  sum += sample * sample;
  swappedSum += (signed / 32768 * gain) ** 2;
  codes.set(code, (codes.get(code) || 0) + 1);
  count++;
}
console.log(JSON.stringify({ filename, channel: channel + 1, count,
  pcm16GridFraction: closeToInteger / count,
  originalRmsDbFs: 10 * Math.log10(sum / count),
  hypotheticalByteSwapRmsDbFs: 10 * Math.log10(swappedSum / count),
  distinctCodes: codes.size,
  mostCommonCodes: [...codes].sort((a,b) => b[1]-a[1]).slice(0, 24)
    .map(([code,n]) => ({ code, hex: (code & 65535).toString(16).padStart(4,'0'), count: n }))
}));
