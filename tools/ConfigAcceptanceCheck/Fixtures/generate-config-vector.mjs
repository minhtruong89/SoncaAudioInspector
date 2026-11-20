// Test only. Generates a new ephemeral key; writes only its public key and signed test data.
import {generateKeyPairSync, createHash, sign} from 'node:crypto';
import {writeFileSync} from 'node:fs';
const {privateKey, publicKey} = generateKeyPairSync('ec', {namedCurve:'prime256v1'});
const speakerModel = 'Loa Việt "A"\\\b\f\n\r\t\u0001<&>😀';
const configJson = JSON.stringify({model:speakerModel,testItems:{InOut:{Devices:{Input:{USB:'Loa'},Output:{MIC:'Mic'}},Tests:[{id:'IO001','Playback Out':'USB','Recording In':'MIC'}]}}});
const revisionId = 'thử-"\\\n\t\u001f';
const serial = 27;
const configSha256 = createHash('sha256').update(configJson,'utf8').digest('hex');
const payload = JSON.stringify({v:1,speakerModel,revisionId,serial,configSha256});
const signature = sign('sha256',Buffer.from(payload,'utf8'),{key:privateKey,dsaEncoding:'ieee-p1363'}).toString('base64');
const variant = serial => {
    const payload = JSON.stringify({v:1,speakerModel,revisionId,serial,configSha256});
    return {speakerModel,revisionId,serial,configJson,configSha256,
        signature:sign('sha256',Buffer.from(payload,'utf8'),{key:privateKey,dsaEncoding:'ieee-p1363'}).toString('base64')};
};
writeFileSync(process.argv[2], JSON.stringify({testOnly:true,speakerModel,revisionId,serial,configJson,configSha256,payload,
    payloadUtf8Base64:Buffer.from(payload,'utf8').toString('base64'),signature,publicKeyPem:publicKey.export({type:'spki',format:'pem'}),
    olderRevision:variant(26),newerRevision:variant(28)},null,2)+'\n');
