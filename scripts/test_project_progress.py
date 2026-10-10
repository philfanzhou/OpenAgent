import copy
import datetime as dt
import json
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest.mock import patch

import project_progress as progress


CONFIG = {'repository':'example/project','start_date':'2026-10-12','developers':1,
          'hours_per_day':8,'working_weekdays':[0,1,2,3,4],'holidays':[],
          'task_label':'functional-task','production_label':'production-first'}


def issue(number, hours=8, parents=(), state='OPEN', reason=None, labels=(), assignees=()):
    values = {'number':number,'module':'模块','priority':'P0','estimate_hours':hours,
              'goal':'功能目标','acceptance_criteria':'结果可查询','predecessors':'无'}
    body = '\n\n'.join('### '+heading+'\n\n'+str(values[key]) for key,heading in progress.HEADINGS.items())
    return {'number':number,'databaseId':number*100,'title':f'Task {number}','body':body,
            'url':f'https://github.com/example/project/issues/{number}','state':state,'stateReason':reason,
            'createdAt':'2026-10-01T00:00:00Z','updatedAt':'2026-10-10T00:00:00Z',
            'closedAt':'2026-10-09T10:00:00Z' if state=='CLOSED' else None,
            'labels':{'nodes':[{'name':n} for n in ['functional-task','production-first',*labels]]},
            'assignees':{'nodes':[{'login':n} for n in assignees]},
            'blockedBy':{'nodes':[{'number':n,'repository':{'nameWithOwner':'example/project'}} for n in parents]}}


def run_schedule(issues, config=None):
    config = copy.deepcopy(config or CONFIG)
    tasks = [progress.parse_issue(i,config) for i in issues]
    summary = progress.schedule(tasks,issues,config,dt.date(2026,10,10))
    return tasks,summary


class ProgressTests(unittest.TestCase):
    def test_capacity_does_not_parallelize_single_developer(self):
        tasks,summary = run_schedule([issue(1,12),issue(2,8)])
        self.assertEqual(tasks[0]['plan']['finish'],'2026-10-13T13:00')
        self.assertEqual(tasks[1]['plan']['start'],'2026-10-13T13:00')
        self.assertEqual(summary['remaining_estimate_hours'],20)

    def test_native_dependencies_override_stale_body(self):
        tasks,_ = run_schedule([issue(1,8,parents=[2]),issue(2,16)])
        self.assertEqual(tasks[0]['predecessors'],'#2')
        self.assertEqual(tasks[0]['plan']['start'],'2026-10-14T09:00')

    def test_completed_then_reopened_updates_forecast(self):
        _,complete = run_schedule([issue(1,state='CLOSED',reason='COMPLETED'),issue(2,parents=[1])])
        _,reopened = run_schedule([issue(1),issue(2,parents=[1])])
        self.assertEqual(complete['remaining_estimate_hours'],8)
        self.assertEqual(reopened['remaining_estimate_hours'],16)
        self.assertLess(complete['all_forecast_finish'],reopened['all_forecast_finish'])

    def test_cancelled_parent_does_not_unlock_dependent(self):
        tasks,summary = run_schedule([issue(1,state='CLOSED',reason='NOT_PLANNED'),issue(2,parents=[1])])
        self.assertIn('blocked_reason',tasks[1]['plan'])
        self.assertIsNone(summary['production_forecast_finish'])

    def test_blocked_chain_leaves_independent_work_schedulable(self):
        tasks,summary = run_schedule([issue(1,labels=['status:blocked']),issue(2,parents=[1]),issue(3)])
        self.assertEqual(summary['unscheduled_tasks'],2)
        self.assertIn('finish',tasks[2]['plan'])
        self.assertIsNone(summary['all_forecast_finish'])

    def test_cycle_fails_instead_of_silently_shortening_plan(self):
        with self.assertRaisesRegex(ValueError,'cycle'):
            run_schedule([issue(1,parents=[2]),issue(2,parents=[1])])

    def test_missing_dependency_blocks_plan(self):
        tasks,summary = run_schedule([issue(1,parents=[999])])
        self.assertIn('不存在',tasks[0]['plan']['blocked_reason'])
        self.assertEqual(summary['unscheduled_tasks'],1)

    def test_calendar_weekends_holidays_and_fractional_hours(self):
        config = {**CONFIG,'holidays':['2026-10-12']}
        self.assertEqual(progress.calendar_time(dt.date(2026,10,10),0,config).date(),dt.date(2026,10,13))
        self.assertEqual(progress.calendar_time(dt.date(2026,10,12),40,CONFIG).date(),dt.date(2026,10,19))
        self.assertEqual(progress.calendar_time(dt.date(2026,10,12),40,CONFIG,end=True).date(),dt.date(2026,10,16))
        self.assertEqual(progress.calendar_time(dt.date(2026,10,12),8,CONFIG,end=True).hour,17)
        tasks,_ = run_schedule([issue(1,4.5)])
        self.assertEqual(tasks[0]['plan']['finish'],'2026-10-12T13:30')

    def test_same_assignee_never_runs_two_tasks_in_parallel(self):
        tasks,_ = run_schedule([issue(1,assignees=['person']),issue(2,assignees=['person'])],{**CONFIG,'developers':2})
        self.assertGreaterEqual(tasks[1]['plan']['start_hours'],tasks[0]['plan']['finish_hours'])
        self.assertEqual(tasks[1]['assignee'],'person')

    def test_missing_hours_fails_instead_of_becoming_zero(self):
        raw = issue(1)
        raw['body'] = raw['body'].replace('### 预计工时（人时）\n\n8','### 预计工时（人时）\n\n_No response_')
        with self.assertRaisesRegex(ValueError,'missing'):
            progress.parse_issue(raw,CONFIG)

    def test_existing_unestimated_issues_remain_visible_and_html_is_escaped(self):
        raw = issue(1)
        raw['title'] = '<script>alert(1)</script>'
        legacy = issue(99)
        legacy['labels']['nodes'] = []
        with tempfile.TemporaryDirectory() as directory:
            report = progress.write_report([raw,legacy],CONFIG,Path(directory),dt.date(2026,10,10))
            self.assertEqual(len(report['unestimated_issues']),1)
            svg = (Path(directory)/'gantt.svg').read_text()
            ET.fromstring(svg)
            self.assertNotIn('<script>',svg)
            self.assertIn('&lt;script&gt;',svg)
            exported = json.loads((Path(directory)/'progress.json').read_text())
            self.assertEqual(exported['summary']['remaining_estimate_hours'],8)

    def test_create_retry_reuses_issue_and_preserves_live_edits(self):
        raw = issue(1)
        raw['body'] = '<!-- openagent-functional-task:batch:1 -->\n'+raw['body']
        labels = [{'name':n} for n in ['functional-task','production-first','P0','P1','P2','status:in-progress','status:blocked','status:review']]
        task = {'number':1,'title':'Task 1','module':'模块','priority':'P0','estimate_hours':8,
                'goal':'原始目标','acceptance_criteria':'原始标准','predecessors':[]}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'docs/planning').mkdir(parents=True)
            with patch.object(progress,'ROOT',root),patch.object(progress,'fetch_issues',return_value=[raw]),patch.object(progress,'github',return_value=labels) as api:
                progress.create_issues({'batch':'batch','tasks':[task],'production_numbers':[1]},CONFIG,True)
                self.assertEqual(api.call_count,1)
                self.assertNotIn('PATCH',str(api.call_args_list))

    def test_removed_task_label_cannot_hide_an_indexed_task(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'docs/planning').mkdir(parents=True)
            (root/'docs/planning/functional-issues.json').write_text(json.dumps({
                'repository':'example/project','issues':[{'issue_number':99}]}))
            with patch.object(progress,'ROOT',root),self.assertRaisesRegex(ValueError,'missing or not classified'):
                progress.write_report([issue(1)],CONFIG,root/'report',dt.date(2026,10,10))

    def test_production_scope_includes_ancestor_and_precedes_extension(self):
        extension = issue(1)
        extension['labels']['nodes'] = [{'name':'functional-task'}]
        ancestor = issue(2)
        ancestor['labels']['nodes'] = [{'name':'functional-task'}]
        tasks,summary = run_schedule([extension,ancestor,issue(3,parents=[2])])
        self.assertEqual(summary['production_tasks'],2)
        self.assertEqual(tasks[1]['plan']['start'],'2026-10-12T09:00')
        self.assertLess(tasks[2]['plan']['finish'],tasks[0]['plan']['finish'])

    def test_all_completed_reports_completion_without_new_forecast_work(self):
        tasks,summary = run_schedule([issue(1,state='CLOSED',reason='COMPLETED')])
        self.assertEqual(summary['all_status'],'已完成')
        self.assertEqual(summary['production_status'],'已完成')
        self.assertEqual(summary['remaining_estimate_hours'],0)
        self.assertIsNone(summary['all_forecast_finish'])
        self.assertNotIn('start',tasks[0]['plan'])


if __name__ == '__main__':
    unittest.main()
