// Run only when a progress workbook is needed. GitHub-derived JSON is the input.
import fs from 'node:fs/promises';
import path from 'node:path';
import {createRequire} from 'node:module';
import {pathToFileURL} from 'node:url';
const args=process.argv.slice(2);
const option=(name,fallback)=>{const i=args.indexOf(name);return i<0?fallback:args[i+1];};
const input=option('--input','outputs/project-progress/progress.json');
const output=option('--output','outputs/project-progress/progress.xlsx');
// Resolve the documented artifact API from the caller's runtime; no machine path is stored in Git.
const runtime=process.env.OPENAGENT_ARTIFACT_RUNTIME;
const resolve=createRequire(runtime?path.join(path.resolve(runtime),'package.json'):import.meta.url);
let modulePath;
try{modulePath=resolve.resolve('@oai/artifact-tool');}
catch{throw new Error('Provide @oai/artifact-tool via OPENAGENT_ARTIFACT_RUNTIME (the parent of runtime node_modules).');}
const {Workbook,SpreadsheetFile}=await import(pathToFileURL(modulePath).href);
const data=JSON.parse(await fs.readFile(input,'utf8'));
const fields=[['number','序号'],['title','任务名称'],['module','任务模块'],['priority','优先级'],['estimate_hours','预计工时（小时）'],['goal','任务目标'],['acceptance_criteria','验收标准'],['predecessors','前序任务'],['issue_url','Issue地址'],['status','状态'],['assignee','负责人']];
if(!data.generated_at||!data.repository||!data.tasks?.length)throw new Error('Expected a nonempty report generated from GitHub Issues.');
const wb=Workbook.create(),sheet=wb.worksheets.add('项目管制表'),last=data.tasks.length+3;
sheet.getRange('A1:H1').merge();
sheet.getRange('A1').values=[[`来源：GitHub ${data.repository} · 抓取：${data.generated_at} · 按需生成，请在 Issue 中更新进度`]];
sheet.getRange('A3:K3').values=[fields.map(([,title])=>title)];
const literal=value=>typeof value==='string'&&/^[=+@-]/.test(value)?`'${value}`:value;
const matrix=data.tasks.map(task=>fields.map(([key])=>literal(task[key]??null)));
sheet.getRange(`A4:K${last}`).values=matrix;
sheet.tables.add(`A3:K${last}`,true,'ProjectControlTasks').style='TableStyleMedium2';
sheet.showGridLines=false;
sheet.getRange(`A1:K${last}`).format.font={name:'Arial',size:11,color:'#263445'};
sheet.getRange(`A1:K${last}`).format.wrapText=true;
sheet.getRange(`A1:K${last}`).format.verticalAlignment='center';
sheet.getRange('A1:H1').format.rowHeight=28;
sheet.getRange('A3:K3').format={fill:'#25445F',font:{name:'Arial',size:11,color:'#FFFFFF',bold:true},rowHeight:36,horizontalAlignment:'center',verticalAlignment:'center'};
for(const [column,width] of [['A',8],['B',38],['C',18],['D',10],['E',18],['F',48],['G',76],['H',24],['I',48],['J',14],['K',20]])sheet.getRange(`${column}1:${column}${last}`).format.columnWidth=width;
data.tasks.forEach((task,i)=>sheet.getRange(`A${i+4}:K${i+4}`).format.rowHeight=Math.max(80,task.acceptance_criteria.split('\n').reduce((n,line)=>n+Math.max(1,Math.ceil([...line].length/34)),0)*16+14));
sheet.getRange(`A4:A${last}`).setNumberFormat('0');
data.tasks.forEach((task,i)=>sheet.getRange(`E${i+4}`).setNumberFormat(Number.isInteger(task.estimate_hours)?'0':'0.##'));
sheet.freezePanes.freezeRows(3);sheet.freezePanes.freezeColumns(2);
wb.recalculate();
if(JSON.stringify(sheet.getRange(`A4:K${last}`).values)!==JSON.stringify(matrix))throw new Error('Workbook data mismatch');
await fs.mkdir(path.dirname(output),{recursive:true});
if(args.includes('--preview')){const preview=await wb.render({sheetName:'项目管制表',range:'A1:H6',scale:1,format:'png'});await fs.writeFile(`${output}.png`,new Uint8Array(await preview.arrayBuffer()));}
const file=await SpreadsheetFile.exportXlsx(wb);await file.save(output);
console.log(`Exported ${data.tasks.length} Issues from ${data.generated_at} to ${output}`);
