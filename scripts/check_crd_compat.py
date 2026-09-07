#!/usr/bin/env python3
"""Fails if a CRD schema (deploy/crds/*.yaml) removes a field/enum value or narrows a type
compared to the same version at the most recent git tag, without a new spec.versions[] entry
alongside it (i.e. no version bump). Skips cleanly when no tag exists yet."""
import glob
import subprocess
import sys

import yaml


def run(*args):
    return subprocess.run(args, capture_output=True, text=True)


def latest_tag():
    result = run("git", "describe", "--tags", "--abbrev=0")
    return result.stdout.strip() if result.returncode == 0 else None


def load_yaml_at(ref, path):
    git_path = path.replace("\\", "/")
    result = run("git", "show", f"{ref}:{git_path}")
    if result.returncode != 0:
        return None
    return yaml.safe_load(result.stdout)


def flatten_schema(schema, prefix=""):
    """Yields (path, type, enum) for every object/leaf node in an openAPIV3Schema."""
    if not isinstance(schema, dict):
        return
    yield prefix or "$", schema.get("type"), tuple(schema.get("enum", []) or [])
    for name, sub in (schema.get("properties") or {}).items():
        yield from flatten_schema(sub, f"{prefix}.{name}" if prefix else name)
    items = schema.get("items")
    if isinstance(items, dict):
        yield from flatten_schema(items, f"{prefix}[]")


def versions_by_name(doc):
    return {v["name"]: v for v in (doc.get("spec", {}).get("versions") or [])}


def check(path, tag):
    new_doc = yaml.safe_load(open(path))
    old_doc = load_yaml_at(tag, path)
    if old_doc is None:
        print(f"[skip] {path}: did not exist at {tag}")
        return []

    old_versions = versions_by_name(old_doc)
    new_versions = versions_by_name(new_doc)
    problems = []
    for name, old_version in old_versions.items():
        new_version = new_versions.get(name)
        if new_version is None:
            continue  # version removed entirely -- a bigger, separately-visible change
        old_schema = (old_version.get("schema") or {}).get("openAPIV3Schema", {})
        new_schema = (new_version.get("schema") or {}).get("openAPIV3Schema", {})
        old_fields = {p: (t, e) for p, t, e in flatten_schema(old_schema)}
        new_fields = {p: (t, e) for p, t, e in flatten_schema(new_schema)}
        for field_path, (old_type, old_enum) in old_fields.items():
            if field_path not in new_fields:
                problems.append(f"{path} [{name}]: field '{field_path}' was removed")
                continue
            new_type, new_enum = new_fields[field_path]
            if old_type and new_type and old_type != new_type:
                problems.append(f"{path} [{name}]: field '{field_path}' type changed {old_type} -> {new_type}")
            missing_enum = set(old_enum) - set(new_enum)
            if missing_enum:
                problems.append(f"{path} [{name}]: field '{field_path}' enum value(s) removed: {sorted(missing_enum)}")

    if problems and set(new_versions) > set(old_versions):
        added = sorted(set(new_versions) - set(old_versions))
        print(f"[ok] {path}: breaking change(s) accompanied by new version entry {added}")
        return []
    return problems


def main():
    tag = latest_tag()
    if not tag:
        print("[skip] no git tag found yet; nothing to compare CRDs against")
        return

    all_problems = []
    for path in sorted(glob.glob("deploy/crds/*.yaml")):
        all_problems.extend(check(path, tag))

    if all_problems:
        print(f"CRD compatibility check failed (comparing against {tag}):")
        for problem in all_problems:
            print(f"  - {problem}")
        sys.exit(1)
    print(f"CRD compatibility check passed (comparing against {tag}).")


if __name__ == "__main__":
    main()
