"""One-shot sandbox entry point. Everything emitted by user code is untrusted data."""
import os
import sys

# The entry runs with python -I, which drops the script directory from sys.path.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import execution_core as core


def main():
    language = os.environ.get("EXECUTION_LANGUAGE", "python")
    entry = os.environ.get("EXECUTION_ENTRY")
    command = core.entry_command(language, entry, os.environ.get("EXECUTION_NODE", "/usr/bin/node"))
    result = core.run_child(command, timeout=int(os.environ["EXECUTION_TIMEOUT"]))
    if result["timedOut"]:
        result["stderr"] = "Execution exceeded its deadline."
    elif result["exitCode"] == 0:
        try:
            result["files"] = core.collect_files()
        except (ValueError, OSError) as error:
            result["exitCode"] = 1
            result["stderr"] = str(error)[:core.LOG_LIMIT]
    print(core.result_json(result), flush=True)


if __name__ == "__main__":
    main()
