#!/usr/bin/env python3
"""Resolve one successful POS staging dry-run through the GitHub API."""
import json
import os
import urllib.request

repo = os.environ.get('GITHUB_REPOSITORY', '')
token = os.environ.get('GITHUB_TOKEN', '')
run_id = os.environ.get('STAGING_RUN_ID', '')

if not repo or not token or not run_id.isdigit():
    raise SystemExit('A numeric staging Run ID and GitHub token are required')


def get(path):
    request = urllib.request.Request(
        'https://api.github.com/repos/' + repo + path,
        headers={
            'Authorization': 'Bearer ' + token,
            'Accept': 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2022-11-28',
        },
    )
    with urllib.request.urlopen(request, timeout=20) as response:
        return json.load(response)


run = get('/actions/runs/' + run_id)
if (
    run.get('repository', {}).get('full_name') != repo
    or run.get('head_branch') != 'staging'
    or run.get('status') != 'completed'
    or run.get('conclusion') != 'success'
):
    raise SystemExit('Run is not a successful POS staging dry run for this repository')

workflow = get('/actions/workflows/' + str(run.get('workflow_id')))
if workflow.get('path') != '.github/workflows/staging-dry-run.yml' or workflow.get('name') != 'POS staging dry run':
    raise SystemExit('Run is not the approved POS staging dry-run workflow')

commit_sha = run.get('head_sha', '')
expected = 'anise-pos-staging-' + commit_sha
matches = [
    artifact
    for artifact in get('/actions/runs/' + run_id + '/artifacts').get('artifacts', [])
    if artifact.get('name') == expected and not artifact.get('expired')
]
if len(matches) != 1:
    raise SystemExit('Expected one non-expired POS staging artifact; found ' + str(len(matches)))

with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
    output.write('artifact_name=' + expected + '\ncommit_sha=' + commit_sha + '\n')
print('Resolved approved POS staging dry run artifact ' + expected)
