"""Create claimable Issues and generate read-only progress reports from GitHub."""
import argparse
import csv
import datetime as dt
import html
import json
import math
import re
import subprocess
import time
from pathlib import Path
from zoneinfo import ZoneInfo

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / 'docs/planning/project-schedule.json'
FIELDS = ['number', 'title', 'module', 'priority', 'estimate_hours', 'goal',
          'acceptance_criteria', 'predecessors', 'issue_url', 'status', 'assignee']
HEADINGS = {'number': '序号', 'module': '任务模块', 'priority': '优先级',
            'estimate_hours': '预计工时（人时）', 'goal': '任务目标',
            'acceptance_criteria': '验收标准', 'predecessors': '前序任务'}
LAST_WRITE = 0.0


def github(endpoint, payload=None):
    global LAST_WRITE
    command = ['gh', 'api', endpoint]
    if payload is not None:
        time.sleep(max(0, 1.3 - (time.monotonic() - LAST_WRITE)))
        command += ['--method', 'POST', '--input', '-']
    result = subprocess.run(command, input=json.dumps(payload) if payload is not None else None,
                            text=True, capture_output=True)
    if payload is not None:
        LAST_WRITE = time.monotonic()
    if result.returncode:
        raise RuntimeError(result.stderr.strip())
    value = json.loads(result.stdout)
    if isinstance(value, dict) and value.get('errors'):
        raise RuntimeError(json.dumps(value['errors'], ensure_ascii=False))
    return value


def fetch_issues(repository):
    owner, name = repository.split('/')
    cursor, result = None, []
    while True:
        query = '''query($owner:String!,$name:String!,$cursor:String){
          repository(owner:$owner,name:$name){issues(first:100,after:$cursor,states:[OPEN,CLOSED],
          orderBy:{field:CREATED_AT,direction:ASC}){nodes{
            number databaseId title body url state stateReason createdAt updatedAt closedAt
            labels(first:100){nodes{name} pageInfo{hasNextPage}}
            assignees(first:100){nodes{login} pageInfo{hasNextPage}}
            blockedBy(first:100){nodes{number repository{nameWithOwner}} pageInfo{hasNextPage}}
          } pageInfo{hasNextPage endCursor}}}}'''
        page = github('graphql', {'query': query, 'variables': {
            'owner': owner, 'name': name, 'cursor': cursor}})['data']['repository']['issues']
        for issue in page['nodes']:
            for field in ['labels', 'assignees', 'blockedBy']:
                if issue[field]['pageInfo']['hasNextPage']:
                    raise ValueError(f'Issue #{issue["number"]}: more than 100 {field}; cannot truncate')
            result.append(issue)
        if not page['pageInfo']['hasNextPage']:
            return result
        cursor = page['pageInfo']['endCursor']


def sections(body):
    matches = list(re.finditer(r'^### (.+)\s*$', body or '', re.M))
    return {match[1].strip(): body[match.end():matches[i+1].start() if i+1 < len(matches) else len(body)].strip()
            for i, match in enumerate(matches)}


def parse_issue(issue, config):
    values = sections(issue['body'])
    def required(key):
        value = values.get(HEADINGS[key], '')
        if not value or value == '_No response_':
            raise ValueError(f'Issue #{issue["number"]}: missing {HEADINGS[key]}')
        return value
    hours = float(required('estimate_hours'))
    if not math.isfinite(hours) or hours <= 0:
        raise ValueError(f'Issue #{issue["number"]}: invalid hours')
    priority = required('priority')
    if priority not in ['P0', 'P1', 'P2']:
        raise ValueError(f'Issue #{issue["number"]}: invalid priority')
    number_text = values.get('序号', '')
    number = int(number_text) if number_text and number_text != '_No response_' else issue['number']
    if number <= 0:
        raise ValueError('Sequence must be positive')
    labels = {label['name'] for label in issue['labels']['nodes']}
    completed = issue['state'] == 'CLOSED' and issue['stateReason'] == 'COMPLETED'
    cancelled = issue['state'] == 'CLOSED' and not completed
    status = ('已完成' if completed else '已取消' if cancelled else '阻塞' if 'status:blocked' in labels
              else '待评审' if 'status:review' in labels else '进行中' if 'status:in-progress' in labels
              else '待开发' if issue['assignees']['nodes'] else '待认领')
    dependencies = [f'{node["repository"]["nameWithOwner"]}#{node["number"]}'
                    for node in issue['blockedBy']['nodes']]
    return {'number': number, 'title': issue['title'], 'module': required('module'),
            'priority': priority, 'estimate_hours': hours, 'goal': required('goal'),
            'acceptance_criteria': required('acceptance_criteria'),
            'predecessors': ';'.join('#'+dep.split('#')[1] if dep.split('#')[0] == config['repository'] else dep
                                     for dep in dependencies),
            'issue_url': issue['url'], 'status': status,
            'assignee': ', '.join(node['login'] for node in issue['assignees']['nodes']),
            'issue_number': issue['number'], 'dependencies': dependencies,
            'completed': completed, 'cancelled': cancelled, 'closed_at': issue['closedAt'],
            'production': config['production_label'] in labels}


def validate_graph(tasks, repository):
    by_number = {task['issue_number']: task for task in tasks}
    visited, visiting = set(), set()
    def visit(number):
        if number in visiting:
            raise ValueError(f'Dependency cycle at #{number}')
        if number in visited:
            return
        visiting.add(number)
        for ref in by_number[number]['dependencies']:
            repo, parent = ref.split('#')
            if repo == repository and int(parent) in by_number:
                visit(int(parent))
        visiting.remove(number)
        visited.add(number)
    for number in by_number:
        visit(number)
    if len({t['number'] for t in tasks}) != len(tasks):
        raise ValueError('Duplicate task sequence; fix the Issue metadata')


def calendar_time(start, offset, config, end=False):
    day_hours = config['hours_per_day']
    if end and offset > 0 and math.isclose(offset % day_hours, 0, abs_tol=1e-8):
        days, hours = round(offset / day_hours)-1, day_hours
    else:
        days, hours = divmod(offset, day_hours)
    date = start
    count = 0
    while True:
        if date.weekday() in config['working_weekdays'] and date.isoformat() not in config['holidays']:
            if count == int(days):
                return dt.datetime.combine(date, dt.time(9)) + dt.timedelta(hours=hours)
            count += 1
        date += dt.timedelta(days=1)


def schedule(tasks, issues, config, as_of):
    if config['developers'] < 1 or config['hours_per_day'] <= 0 or not config['working_weekdays']:
        raise ValueError('Invalid capacity')
    if len(set(config['working_weekdays'])) != len(config['working_weekdays']) or any(d not in range(7) for d in config['working_weekdays']):
        raise ValueError('Invalid work calendar')
    validate_graph(tasks, config['repository'])
    start = max(dt.date.fromisoformat(config['start_date']), as_of)
    known = {issue['number']: issue for issue in issues}
    by_number = {task['issue_number']: task for task in tasks}
    done = {t['issue_number']: 0.0 for t in tasks if t['completed']}
    slots = [0.0] * config['developers']
    owners = {}
    pending = [task for task in tasks if not task['completed'] and not task['cancelled']]
    production = {t['issue_number'] for t in tasks if t['production']}
    # Include all managed ancestors of the production scope.
    stack = list(production)
    while stack:
        for ref in by_number[stack.pop()]['dependencies']:
            repo, number = ref.split('#')
            number = int(number)
            if repo == config['repository'] and number in by_number and number not in production:
                production.add(number)
                stack.append(number)
    def dependency_state(task):
        ready_at = 0.0
        for ref in task['dependencies']:
            repo, number = ref.split('#')
            number = int(number)
            if repo != config['repository']:
                return None, f'外部依赖待确认：{ref}'
            parent = known.get(number)
            if parent is None:
                return None, f'依赖不存在：#{number}'
            if parent['state'] == 'CLOSED' and parent['stateReason'] == 'COMPLETED':
                continue
            if number not in done:
                return None, f'依赖尚未排期或未完成：#{number}'
            ready_at = max(ready_at, done[number])
        return ready_at, None
    while pending:
        ready = [(t, dependency_state(t)[0]) for t in pending
                 if t['status'] != '阻塞' and dependency_state(t)[0] is not None]
        if not ready:
            break
        task, earliest = min(ready, key=lambda pair: (
            pair[0]['issue_number'] not in production, pair[0]['status'] != '进行中',
            pair[0]['priority'], pair[0]['number']))
        slot = min(range(len(slots)), key=lambda i: slots[i])
        names = task['assignee'].split(', ') if task['assignee'] else []
        begin = max([slots[slot], earliest] + [owners.get(name, 0.0) for name in names])
        finish = begin + task['estimate_hours']
        task['plan'] = {'start': calendar_time(start, begin, config).isoformat(timespec='minutes'),
                        'finish': calendar_time(start, finish, config, end=True).isoformat(timespec='minutes'),
                        'capacity_slot': slot+1, 'start_hours': begin, 'finish_hours': finish}
        slots[slot] = done[task['issue_number']] = finish
        for name in names:
            owners[name] = finish
        pending.remove(task)
    for task in pending:
        task['plan'] = {'blocked_reason': 'Issue 标记为阻塞' if task['status'] == '阻塞' else dependency_state(task)[1]}
    for task in tasks:
        if task['completed'] or task['cancelled']:
            task['plan'] = {'actual_finish': task['closed_at']}
    def forecast(scope):
        if any(t['cancelled'] for t in scope):
            return None
        active = [t for t in scope if not t['completed'] and not t['cancelled']]
        if any(not t['plan'].get('finish') for t in active):
            return None
        return max((t['plan']['finish'] for t in active), default=None)
    def stage_status(scope):
        if not scope:
            return '未定义范围'
        if all(t['completed'] for t in scope):
            return '已完成'
        if any(t['cancelled'] for t in scope):
            return '范围含取消项，待调整'
        if any(t['plan'].get('blocked_reason') for t in scope):
            return '存在阻塞，暂无完整预测'
        return '待完成'
    production_scope = [by_number[n] for n in production]
    return {'total_tasks': len(tasks), 'completed_tasks': sum(t['completed'] for t in tasks),
            'cancelled_tasks': sum(t['cancelled'] for t in tasks),
            'total_estimate_hours': sum(t['estimate_hours'] for t in tasks),
            'remaining_estimate_hours': sum(t['estimate_hours'] for t in tasks if not t['completed'] and not t['cancelled']),
            'unscheduled_tasks': len(pending), 'production_tasks': len(production),
            'production_status': stage_status(production_scope), 'all_status': stage_status(tasks),
            'production_forecast_finish': forecast(production_scope),
            'all_forecast_finish': forecast(tasks), 'effective_start': start.isoformat()}


def render_svg(tasks, config, summary):
    start = dt.date.fromisoformat(summary['effective_start'])
    end = max([start+dt.timedelta(days=7)] + [dt.datetime.fromisoformat(t['plan']['finish']).date()
                                             for t in tasks if t['plan'].get('finish')])
    days = (end-start).days+2
    left, scale, row_height = 470, 9, 36
    width, height = left+days*scale+170, 70+len(tasks)*row_height
    esc = html.escape
    chunks = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-label="OpenAgent Issue 甘特图">',
              '<style>text{font:12px Arial,"Noto Sans CJK SC",sans-serif}a{text-decoration:none} .title{font-size:14px;font-weight:bold}</style>',
              f'<rect width="{width}" height="{height}" fill="white"/>',
              '<text x="16" y="23" class="title">OpenAgent · Issue 状态与预测排期</text>',
              f'<text x="16" y="45">{config["developers"]} 名开发 · 每日 {config["hours_per_day"]} 人时 · 每周 {len(config["working_weekdays"])} 个有效人日 · 蓝=P0 黄=P1 紫=P2 绿=已完成 红=阻塞</text>']
    for day in range(days):
        date = start+dt.timedelta(days=day)
        x = left+day*scale
        if date.weekday() not in config['working_weekdays'] or date.isoformat() in config['holidays']:
            chunks.append(f'<rect x="{x}" y="56" width="{scale}" height="{height-56}" fill="#f1f5f9"/>')
        if date.weekday() == 0:
            chunks.append(f'<path d="M{x} 55 V{height}" stroke="#dbe3ed"/><text x="{x+2}" y="65">{date:%m-%d}</text>')
    for i, task in enumerate(tasks):
        y = 78+i*row_height
        attrs = ' '.join(f'data-{key}="{esc(str(task[key]), quote=True)}"' for key in ['priority','module','status'])
        chunks.append(f'<g data-task="true" {attrs} data-title="{esc(task["title"],quote=True)}" transform="translate(0,{y})">')
        chunks.append(f'<path d="M0 24 H{width}" stroke="#e8edf3"/><a href="{esc(task["issue_url"],quote=True)}" target="_blank"><text x="16" y="13">{task["number"]}. #{task["issue_number"]} {esc(task["title"])}</text></a>')
        plan = task['plan']
        if plan.get('start'):
            begin, finish = (dt.datetime.fromisoformat(plan[k]) for k in ['start','finish'])
            def position(value):
                return left+((value.date()-start).days+(value.hour-9+value.minute/60)/config['hours_per_day'])*scale
            x = position(begin)
            bar_width = max(4, position(finish)-x)
            color = {'P0':'#2563eb','P1':'#d97706','P2':'#7c3aed'}[task['priority']]
            chunks.append(f'<rect x="{x:.2f}" y="-1" width="{bar_width:.2f}" height="18" rx="3" fill="{color}"><title>{esc(task["status"])} · {task["estimate_hours"]:g} 人时 · {begin:%Y-%m-%d} → {finish:%Y-%m-%d} · 前序：{esc(task["predecessors"] or "无")}</title></rect><text x="{left+days*scale+8}" y="13">{begin:%m-%d} → {finish:%m-%d}</text>')
        else:
            message = '已完成（'+(task['closed_at'] or '')[:10]+'）' if task['completed'] else '已取消' if task['cancelled'] else plan['blocked_reason']
            color = '#15803d' if task['completed'] else '#64748b' if task['cancelled'] else '#b91c1c'
            chunks.append(f'<text x="{left+6}" y="13" fill="{color}">{esc(message)}</text>')
        chunks.append('</g>')
    return ''.join(chunks)+'</svg>'


def write_report(issues, config, out, as_of):
    tasks, unestimated = [], []
    index_path = ROOT/'docs/planning/functional-issues.json'
    if index_path.exists():
        index = json.loads(index_path.read_text(encoding='utf-8'))
        if index['repository'] == config['repository']:
            managed = {i['number'] for i in issues if config['task_label'] in {l['name'] for l in i['labels']['nodes']}}
            missing = {entry['issue_number'] for entry in index['issues']} - managed
            if missing:
                raise ValueError(f'Indexed Issues missing or not classified as functional tasks: {sorted(missing)}')
    for issue in issues:
        if config['task_label'] in {label['name'] for label in issue['labels']['nodes']}:
            tasks.append(parse_issue(issue, config))
        elif issue['state'] == 'OPEN':
            unestimated.append({'number':issue['number'],'title':issue['title'],'url':issue['url'],
                                'reason':'未按功能任务模板提供模块、工时及验收边界'})
    if not tasks:
        raise ValueError('No functional tasks found; refusing an empty progress report')
    tasks.sort(key=lambda t:t['number'])
    summary = schedule(tasks, issues, config, as_of)
    out.mkdir(parents=True, exist_ok=True)
    report = {'generated_at':dt.datetime.now(ZoneInfo('Asia/Shanghai')).isoformat(timespec='seconds'),
              'as_of':as_of.isoformat(),'repository':config['repository'],
              'config':config,'summary':summary,'tasks':tasks,'unestimated_issues':unestimated}
    (out/'progress.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    def safe(value):
        return "'"+value if isinstance(value,str) and value.startswith(('=','+','-','@')) else value
    with (out/'progress.csv').open('w',encoding='utf-8-sig',newline='') as file:
        writer = csv.DictWriter(file,fieldnames=FIELDS)
        writer.writeheader()
        writer.writerows({key:safe(task[key]) for key in FIELDS} for task in tasks)
    svg = render_svg(tasks,config,summary)
    (out/'gantt.svg').write_text(svg,encoding='utf-8')
    modules = ''.join(f'<option>{html.escape(module)}</option>' for module in sorted({t['module'] for t in tasks}))
    legacy = ''.join(f'<li><a href="{html.escape(t["url"],quote=True)}">#{t["number"]} {html.escape(t["title"])}</a>：{t["reason"]}</li>' for t in unestimated)
    page = f'''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>OpenAgent 项目排期</title>
<style>body{{margin:24px;font:14px Arial,sans-serif;color:#263445}}header{{max-width:1200px}}select,input{{margin:8px;padding:8px}}.chart{{overflow:auto;border:1px solid #cbd5e1;max-height:75vh}}.note{{color:#64748b}}a{{color:#2563eb}}</style>
<header><h1>OpenAgent 项目进度与甘特图</h1><p>排期基准日 {as_of}，读取当前 GitHub Issue 状态、负责人、工时和原生依赖；抓取生成于 {report['generated_at']}。</p>
<p>{summary['total_tasks']} 条任务 · 已完成 {summary['completed_tasks']} · 剩余 {summary['remaining_estimate_hours']:g} 人时 · 未能排期 {summary['unscheduled_tasks']} 条</p>
<p>生产首版预测完成：{summary['production_forecast_finish'] or summary['production_status']}；全部任务预测完成：{summary['all_forecast_finish'] or summary['all_status']}。</p>
<p class="note">按 {config['developers']} 名开发、每周 {len(config['working_weekdays'])} 个有效人日、每日 {config['hours_per_day']} 人时计算。未提供节假日；仅排除配置中的休息日。未完成任务保留全部估算，不推测实际耗时或完成百分比；不含额外联调缓冲。容量槽位不代表分配负责人。</p>
<input id="search" placeholder="搜索任务或 Issue"><select id="priority"><option value="">全部优先级</option><option>P0</option><option>P1</option><option>P2</option></select><select id="module"><option value="">全部模块</option>{modules}</select><select id="status"><option value="">全部状态</option><option>待认领</option><option>待开发</option><option>进行中</option><option>待评审</option><option>阻塞</option><option>已完成</option><option>已取消</option></select></header>
<div class="chart">{svg}</div><h2>未纳入排期的现有议题</h2><ul>{legacy or '<li>无</li>'}</ul>
<script>const controls=['search','priority','module','status'].map(id=>document.getElementById(id));
function filter(){{let index=0;document.querySelectorAll('g[data-task]').forEach(row=>{{const [q,p,m,s]=controls.map(c=>c.value);const show=(!q||row.textContent.toLowerCase().includes(q.toLowerCase()))&&(!p||row.dataset.priority===p)&&(!m||row.dataset.module===m)&&(!s||row.dataset.status===s);row.style.display=show?'':'none';if(show)row.setAttribute('transform','translate(0,'+(78+index++*36)+')');}});const svg=document.querySelector('svg');svg.setAttribute('height',70+index*36);svg.setAttribute('viewBox','0 0 '+svg.getAttribute('width')+' '+(70+index*36));}}controls.forEach(c=>c.addEventListener('input',filter));</script></html>'''
    (out/'gantt.html').write_text(page,encoding='utf-8')
    print(json.dumps(summary,ensure_ascii=False),flush=True)
    return report


def create_issues(seed, config, apply):
    tasks = seed['tasks']
    # Validate the complete batch before any remote write.
    if len({t['number'] for t in tasks}) != len(tasks):
        raise ValueError('Duplicate seed sequence')
    prior = set()
    for task in tasks:
        if not task['goal'] or not task['acceptance_criteria'] or task['priority'] not in ['P0','P1','P2'] or task['estimate_hours'] <= 0:
            raise ValueError(f'Invalid task {task["number"]}')
        if not set(task['predecessors']).issubset(prior):
            raise ValueError(f'Invalid predecessors: {task["number"]}')
        prior.add(task['number'])
    if not apply:
        print(f'Dry run: {len(tasks)} Issues, {sum(t["estimate_hours"] for t in tasks)} person-hours; use --apply to create.')
        return
    repository = config['repository']
    issues = fetch_issues(repository)
    existing_labels = {label['name'] for label in github(f'repos/{repository}/labels?per_page=100')}
    label_colors = {'functional-task':'2563eb','production-first':'15803d','P0':'b91c1c','P1':'d97706','P2':'7c3aed',
                    'status:in-progress':'2563eb','status:blocked':'b91c1c','status:review':'d97706'}
    for name,color in label_colors.items():
        if name not in existing_labels:
            github(f'repos/{repository}/labels',{'name':name,'color':color})
    mapping = {}
    index_path = ROOT/'docs/planning/functional-issues.json'
    for task in tasks:
        marker = f'<!-- openagent-functional-task:{seed["batch"]}:{task["number"]} -->'
        matches = [i for i in issues if marker in (i['body'] or '')]
        if len(matches) > 1:
            raise ValueError(f'Duplicate marker for task {task["number"]}')
        parents = [mapping[n] for n in task['predecessors']]
        if matches:
            issue = matches[0]
        else:
            if any(i['title']==task['title'] for i in issues):
                raise ValueError(f'Title already exists without batch marker: {task["title"]}')
            values = {**task,'predecessors':'; '.join('#'+str(p['number']) for p in parents) or '无'}
            body = marker+'\n\n'+'\n\n'.join(f'### {heading}\n\n{values[key]}' for key,heading in HEADINGS.items())
            labels = [config['task_label'],task['priority']]
            if task['number'] in seed['production_numbers']:
                labels.append(config['production_label'])
            raw = github(f'repos/{repository}/issues',{'title':task['title'],'body':body,'labels':labels})
            issue = {'number':raw['number'],'databaseId':raw['id'],'url':raw['html_url'],
                     'blockedBy':{'nodes':[]}}
        blocked = {p['number'] for p in issue['blockedBy']['nodes']}
        for parent in parents:
            if parent['number'] not in blocked:
                github(f'repos/{repository}/issues/{issue["number"]}/dependencies/blocked_by',
                       {'issue_id':parent['databaseId']})
        mapping[task['number']] = {'number':issue['number'],'databaseId':issue['databaseId'],'url':issue['url']}
        index_path.write_text(json.dumps({'repository':repository,'batch':seed['batch'],
                              'issues':[{'sequence':n,'issue_number':p['number'],'issue_url':p['url']} for n,p in mapping.items()]},
                              ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print(f'{len(mapping)}/{len(tasks)}: #{issue["number"]} {task["title"]}',flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config',type=Path,default=CONFIG)
    sub = parser.add_subparsers(dest='command',required=True)
    create = sub.add_parser('create')
    create.add_argument('--seed',type=Path,default=ROOT/'docs/planning/functional-task-seed.json')
    create.add_argument('--apply',action='store_true')
    report = sub.add_parser('report')
    report.add_argument('--out',type=Path,default=ROOT/'outputs/project-progress')
    report.add_argument('--as-of',type=dt.date.fromisoformat,
                        default=dt.datetime.now(ZoneInfo('Asia/Shanghai')).date())
    args = parser.parse_args()
    config = json.loads(args.config.read_text(encoding='utf-8'))
    if args.command == 'create':
        create_issues(json.loads(args.seed.read_text(encoding='utf-8')),config,args.apply)
    else:
        write_report(fetch_issues(config['repository']),config,args.out,args.as_of)


if __name__ == '__main__':
    main()
