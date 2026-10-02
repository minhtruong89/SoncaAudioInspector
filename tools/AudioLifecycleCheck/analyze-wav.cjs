const fs = require('fs');
for (const filename of process.argv.slice(2)) {
  const b = fs.readFileSync(filename); let p = 12, data, rate, channels, bits;
  while (p + 8 <= b.length) {
    const name = b.toString('ascii', p, p + 4), n = b.readUInt32LE(p + 4);
    if (name === 'fmt ') { channels = b.readUInt16LE(p + 10); rate = b.readUInt32LE(p + 12); bits = b.readUInt16LE(p + 22); }
    if (name === 'data') data = b.subarray(p + 8, p + 8 + n);
    p += 8 + n + n % 2;
  }
  if (bits !== 32 || !data) throw Error('Expected float32 WAV');
  for (let c = 0; c < channels; c++) {
    const samples = [];
    for (let i = rate; i < 2 * rate && (i * channels + c + 1) * 4 <= data.length; i++) samples.push(data.readFloatLE((i * channels + c) * 4));
    if (samples.length !== rate) continue;
    const rms = Math.sqrt(samples.reduce((s,x)=>s+x*x,0)/samples.length);
    const amps = [];
    for (let h = 1; h <= 9; h++) {
      let re=0, im=0;
      samples.forEach((x,i)=>{ const phase=2*Math.PI*1000*h*i/rate; re+=x*Math.cos(phase); im+=x*Math.sin(phase); });
      amps.push(2*Math.hypot(re,im)/rate);
    }
    console.log(JSON.stringify({filename, channel:c+1, rate, rmsDbFs:20*Math.log10(rms), peak:samples.reduce((p,x)=>Math.max(p,Math.abs(x)),0),
      fundamentalPeak:amps[0], harmonicPercent:amps.slice(1).map(x=>100*x/amps[0]),
      thdH2H9Percent:100*Math.hypot(...amps.slice(1))/amps[0],
      sufficientBroadbandLevel:20*Math.log10(rms)>-70,
      fundamentalPowerFraction:amps[0]*amps[0]/(2*rms*rms),
      toneDominates:amps[0]*amps[0]/(2*rms*rms)>.5}));
  }
}
