# Line coverage of the Lake.Parquet package from one or more cobertura files (dotnet-coverage), merged: a line counts as
# covered if any run covered it. Fails below the threshold (Lake.NET decision 6, testing.md "Coverage").
#
#   python3 eng/lake/coverage.py --min 99 aot.xml tests.xml
import argparse, collections, sys, xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("--min", type=float, required=True, help="the lowest acceptable percentage")
parser.add_argument("files", nargs="+")
args = parser.parse_args()

hits = {}
for path in args.files:
    for cls in ET.parse(path).getroot().iter("class"):
        name = (cls.get("filename") or "").replace("\\", "/")
        if "src/Parquet/" not in name:
            continue
        name = name.split("src/Parquet/")[-1]
        for line in cls.iter("line"):
            key = (name, int(line.get("number")))
            hits[key] = max(hits.get(key, 0), int(line.get("hits")))

total = len(hits)
if total == 0:
    sys.exit("no lines of src/Parquet in the coverage files")
covered = sum(1 for h in hits.values() if h)
percent = 100 * covered / total
print(f"Lake.Parquet line coverage: {percent:.2f}% ({covered} of {total}), minimum {args.min}%")
missed = collections.defaultdict(list)
for (name, number), h in sorted(hits.items()):
    if not h:
        missed[name].append(number)
for name, numbers in sorted(missed.items(), key=lambda kv: -len(kv[1])):
    print(f"  {name}: {len(numbers)} line(s) not run: {', '.join(map(str, numbers[:20]))}{' ...' if len(numbers) > 20 else ''}")
if percent < args.min:
    sys.exit(f"coverage {percent:.2f}% is below the minimum {args.min}%")
