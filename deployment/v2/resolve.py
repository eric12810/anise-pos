"""Resolve one successful V2 staging run and its exact, unexpired artifact."""
import json
import os
import urllib.request


def accepted_branch(run):
    branch = run.get("head_branch")
    return branch == "staging" or (branch in {"release/push-notifications", "release/push-production-safe"}
                                   and run.get("event") == "workflow_dispatch")


def main():
    repo = os.environ["GITHUB_REPOSITORY"]
    run_id = os.environ["STAGING_RUN_ID"]
    token = os.environ["GITHUB_TOKEN"]
    if not run_id.isdigit():
        raise ValueError("Numeric staging run ID required")

    def get(path):
        request = urllib.request.Request("https://api.github.com/repos/" + repo + path,
            headers={"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json"})
        with urllib.request.urlopen(request, timeout=20) as response:
            return json.load(response)

    run = get("/actions/runs/" + run_id)
    workflow = get("/actions/workflows/" + str(run.get("workflow_id")))
    jobs = get("/actions/runs/" + run_id + "/jobs?per_page=100").get("jobs", [])
    if (run.get("repository", {}).get("full_name") != repo
            or not accepted_branch(run)
            or run.get("status") != "completed" or run.get("conclusion") != "success"
            or workflow.get("path") != ".github/workflows/pos-v2-staging.yml"
            or not any(j.get("name") == "Deploy and verify staging" and j.get("conclusion") == "success" for j in jobs)):
        raise ValueError("Run is not a verified V2 staging deployment")
    sha = run.get("head_sha", "")
    name = "anise-pos-v2-" + sha
    artifacts = get("/actions/runs/" + run_id + "/artifacts?per_page=100").get("artifacts", [])
    matches = [a for a in artifacts if a.get("name") == name and not a.get("expired")
               and a.get("workflow_run", {}).get("id") == int(run_id)]
    if len(sha) != 40 or len(matches) != 1:
        raise ValueError("Exact staging artifact missing or expired")
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        output.write("artifact_name=" + name + "\nrelease=" + sha + "\n")
    print("Resolved verified staging artifact", name)


if __name__ == "__main__":
    main()
