"""Fail a CI run whose backend tests didn't all run.

`dotnet test` passes when tests are skipped. The PostgreSQL tests skip themselves when
STOREFRONT_TEST_POSTGRES isn't set, so a mistake in the workflow would hide 21 database tests
behind a green check. This script reads the TRX result files and fails unless every test ran
and passed, and at least the expected number of tests was found.

Usage: python3 tools/ci/check_test_results.py <results directory> <minimum test count>
"""
import glob
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main(results_dir: str, minimum: int) -> int:
    files = glob.glob(f"{results_dir}/**/*.trx", recursive=True)
    if not files:
        print(f"No TRX files under {results_dir}.")
        return 1

    total = executed = passed = 0
    skipped: list[str] = []
    for path in files:
        root = ET.parse(path).getroot()
        counters = root.find("t:ResultSummary/t:Counters", NS)
        total += int(counters.get("total", 0))
        executed += int(counters.get("executed", 0))
        passed += int(counters.get("passed", 0))
        skipped += [r.get("testName") for r in root.iterfind("t:Results/t:UnitTestResult", NS) if r.get("outcome") == "NotExecuted"]

    print(f"{total} tests found, {executed} ran, {passed} passed, {len(skipped)} skipped.")
    for name in skipped[:20]:
        print(f"  skipped: {name}")

    if total < minimum:
        print(f"Expected at least {minimum} tests: some weren't discovered.")
        return 1
    if skipped or executed != total or passed != total:
        print("Every test must run and pass in CI.")
        return 1
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1], int(sys.argv[2])))
