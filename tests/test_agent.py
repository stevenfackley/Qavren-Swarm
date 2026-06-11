"""Pure-function tests for the in-container agent (no Docker, no LLM).

Run: python -m pytest tests/test_agent.py
"""
import os
import pathlib
import shutil
import sys
import tempfile

# Point the agent's WORK dir at a throwaway temp dir BEFORE importing it (WORK is read at
# module load), then import agent.py from the Agent/ build context.
WORKDIR = tempfile.mkdtemp(prefix="qavren-test-")
os.environ["QAVREN_WORK_DIR"] = WORKDIR
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "Agent"))
import agent  # noqa: E402

WORK = pathlib.Path(WORKDIR)


def setup_function(_):
    for p in WORK.glob("*"):
        shutil.rmtree(p) if p.is_dir() else p.unlink()


def test_block_re_parses_edit_and_new_file():
    out = ("FILE: a.py\n<<<<<<< SEARCH\nx = 1\n=======\nx = 2\n>>>>>>> REPLACE\n"
           "FILE: b.py\n<<<<<<< SEARCH\n\n=======\nnew\n>>>>>>> REPLACE\n")
    assert len(list(agent.BLOCK_RE.finditer(out))) == 2


def test_apply_edits_preserves_crlf():
    (WORK / "a.py").write_bytes(b"x = 1\r\n")
    out = "FILE: a.py\n<<<<<<< SEARCH\nx = 1\n=======\nx = 2\n>>>>>>> REPLACE\n"
    applied, failed = agent.apply_edits(out)
    assert applied == 1 and failed == []
    assert (WORK / "a.py").read_bytes() == b"x = 2\r\n"  # CRLF preserved, not flipped to LF


def test_apply_edits_creates_new_file_lf():
    # Empty SEARCH (new file) is represented by a blank line between the markers.
    out = "FILE: new.py\n<<<<<<< SEARCH\n\n=======\nprint(1)\n>>>>>>> REPLACE\n"
    applied, failed = agent.apply_edits(out)
    assert applied == 1 and failed == []
    assert (WORK / "new.py").read_bytes() == b"print(1)\n"


def test_apply_edits_fuzzy_matches_wrong_indentation():
    # The model de-indented the block (a common weak-model error). Exact match fails,
    # but the whitespace-tolerant fallback matches and re-indents to the file's 8 spaces.
    (WORK / "m.py").write_bytes(b"class C:\n    def f(self):\n        return 1\n")
    out = ("FILE: m.py\n<<<<<<< SEARCH\n"
           "def f(self):\n    return 1\n"
           "=======\n"
           "def f(self):\n    return 2\n"
           ">>>>>>> REPLACE\n")
    applied, failed = agent.apply_edits(out)
    assert applied == 1 and failed == []
    assert (WORK / "m.py").read_bytes() == b"class C:\n    def f(self):\n        return 2\n"


def test_apply_edits_reports_nonmatching_hunk():
    (WORK / "a.py").write_bytes(b"x = 1\n")
    out = "FILE: a.py\n<<<<<<< SEARCH\nTOTALLY_ABSENT\n=======\ny\n>>>>>>> REPLACE\n"
    applied, failed = agent.apply_edits(out)
    assert applied == 0
    assert len(failed) == 1 and failed[0]["reason"] == "search not found"


def test_containment_rejects_escape_and_writes_nothing():
    out = "FILE: ../evil.py\n<<<<<<< SEARCH\n\n=======\nbad\n>>>>>>> REPLACE\n"
    applied, failed = agent.apply_edits(out)
    assert applied == 0
    assert any(f["reason"] == "out-of-tree" for f in failed)
    assert not (WORK.parent / "evil.py").exists()
