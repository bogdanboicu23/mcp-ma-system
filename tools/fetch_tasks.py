"""Download SWE-bench Verified into tasks/swebench_verified.jsonl (one JSON object per line).

This is the only Python the project needs besides the official evaluator.

    pip install datasets
    python tools/fetch_tasks.py

The C# runner reads the file with snake_case field names; the dataset stores FAIL_TO_PASS and PASS_TO_PASS as
JSON-encoded strings, which are decoded here into real lists.
"""
import argparse
import json
import os

from datasets import load_dataset


def as_list(value):
    if isinstance(value, str):
        value = json.loads(value)
    return list(value)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dataset", default="princeton-nlp/SWE-bench_Verified")
    parser.add_argument("--split", default="test")
    parser.add_argument("--out", default=os.path.join("tasks", "swebench_verified.jsonl"))
    args = parser.parse_args()

    dataset = load_dataset(args.dataset, split=args.split)
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)

    with open(args.out, "w", encoding="utf-8", newline="\n") as handle:
        for row in dataset:
            record = {
                "instance_id": row["instance_id"],
                "repo": row["repo"],
                "base_commit": row["base_commit"],
                "problem_statement": row["problem_statement"],
                "fail_to_pass": as_list(row["FAIL_TO_PASS"]),
                "pass_to_pass": as_list(row["PASS_TO_PASS"]),
                "patch": row["patch"],
                "test_patch": row["test_patch"],
                "version": str(row.get("version", "")),
            }
            handle.write(json.dumps(record, ensure_ascii=False) + "\n")

    print(f"Wrote {len(dataset)} tasks to {args.out}")


if __name__ == "__main__":
    main()
