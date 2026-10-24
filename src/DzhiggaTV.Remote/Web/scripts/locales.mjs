import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const web=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const parts=fs.readFileSync(path.resolve(web,'../../../Directory.Build.props'),'utf8').match(/<Version>([^<]+)<\/Version>/)[1].split('.');
while(parts.length>2&&parts.at(-1)==='0')parts.pop();
fs.writeFileSync(path.join(web,'src/buildVersion.json'),JSON.stringify({version:parts.join('.')},null,2)+'\n');
const catalog=JSON.parse(fs.readFileSync(path.resolve(web,'../../DzhiggaTV.Localization/Messages.json'),'utf8'));
const result={};
for(const file of fs.readdirSync(path.join(web,'src')).filter(f=>/\.tsx?$/.test(f)&&!f.includes('.test.'))){
  const text=fs.readFileSync(path.join(web,'src',file),'utf8');
  for(const match of text.matchAll(/\bt\(\s*("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')/g)){
    const key=match[1][0]==='"'?JSON.parse(match[1]):match[1].slice(1,-1).replace(/\\'/g,"'");
    if(!catalog[key])throw new Error('Missing UI translation: '+key);
    result[key]=catalog[key];
  }
}
fs.writeFileSync(path.join(web,'src/messages.json'),JSON.stringify(result,null,2)+'\n');
console.log(`Remote: ${Object.keys(result).length} localized messages.`);
