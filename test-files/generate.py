#!/usr/bin/env python3
"""Regenerates every sample file in this directory.

The samples are committed, so you don't need to run this to use them. Run it when you want to
change one, add another, or produce a bigger performance sample than the one that's checked in:

    python3 test-files/generate.py                 # rewrite the committed set
    python3 test-files/generate.py --perf-rows 2000000   # ... with a 2,000,000-row sample instead

Every file is written byte-exactly, line endings included. The DIF samples are marked binary in
.gitattributes so git never rewrites them on checkout — a sample that exists to prove CRLF handling
is worthless if git silently converts it.
"""

import argparse
import random
from pathlib import Path

LF = "\n"
CRLF = "\r\n"
OUT = Path(__file__).resolve().parent


def write(name: str, lines: list[str], eol: str = LF) -> None:
    """Join lines with `eol` (including a trailing one, as real exports have) and write as UTF-8."""
    path = OUT / name
    path.write_bytes(("".join(line + eol for line in lines)).encode("utf-8"))
    print(f"{path.name:<40} {path.stat().st_size:>10,} bytes")


def write_raw(name: str, data: bytes) -> None:
    path = OUT / name
    path.write_bytes(data)
    print(f"{path.name:<40} {path.stat().st_size:>10,} bytes")


def rows_with_implicit_prefix(ids: list[str], field_count: int, values: list[list[str]], delim: str) -> list[str]:
    """Data rows for a file whose START-OF-FIELDS does NOT declare _ID.

    The parser prepends _ID/_ERR/_SIZE to the column list in that case, because every getdata-style
    row carries those three ahead of the requested values whether or not they're declared. So the
    rows written here must carry them too.
    """
    return [delim.join([sec, "0", str(field_count)] + vals) for sec, vals in zip(ids, values)]


# --------------------------------------------------------------------------------------------
# 01-03 — the two header dialects, and the inline DATA= attribute form
# --------------------------------------------------------------------------------------------

def classic_inahdr() -> None:
    """INAHDR/INATRL with _ID and _ERR declared explicitly — no implicit prefix is added."""
    fields = ["_ID", "_ERR", "TICKER", "PX_LAST", "CRNCY", "VOLUME"]
    data = [
        ("AAPL US Equity", "0", "AAPL", "241.84", "USD", "42184300"),
        ("MSFT US Equity", "0", "MSFT", "428.02", "USD", "18422100"),
        ("7203 JT Equity", "0", "7203", "2841.50", "JPY", "9930400"),
        ("VOD LN Equity", "0", "VOD", "68.94", "GBp", "31882000"),
        ("SAP GY Equity", "0", "SAP", "196.72", "EUR", "2211800"),
        ("BADTICKER Equity", "10", "", "", "", ""),
    ]
    write("01-classic-inahdr.dif", [
        "INAHDR",
        "FIRMNAME=dl123456",
        "DELIMITER=|",
        "START-OF-FIELDS", *fields, "END-OF-FIELDS",
        "START-OF-DATA",
        *["|".join(r) for r in data],
        "END-OF-DATA",
        "INATRL",
        f"DATARECORDS={len(data)}",
    ])


def getdata_imahdr() -> None:
    """IMAHDR + START-OF-FILE/END-OF-FILE + IMATRL, with the implicit _ID/_ERR/_SIZE prefix.

    Also exercises metadata sitting between END-OF-FIELDS and START-OF-DATA (TIMESTARTED), which
    real getdata output does and which the parser skips rather than requiring exact adjacency.
    """
    fields = ["TICKER", "CPN", "MATURITY", "YLD_YTM_MID"]
    ids = ["EC3478608 Corp", "BK0849954 Corp", "BK0851679 Corp", "EJ7614855 Corp", "ZQ1234567 Corp"]
    values = [
        ["PRETSL", "9.550000", "20310301", "7.812000"],
        ["ENDP", "6.000000", "20280630", "5.114000"],
        ["ENDP", "9.500000", "20270731", "8.902000"],
        ["T", "4.250000", "20340515", "4.118000"],
        ["BUND", "2.600000", "20330815", "2.341000"],
    ]
    write("02-getdata-imahdr.dif", [
        "IMAHDR",
        "START-OF-FILE",
        "FIRMNAME=dl123456",
        "PROGRAMNAME=getdata",
        "DATEFORMAT=yyyymmdd",
        "ENCODING=UTF-8",
        "DELIMITER=|",
        "START-OF-FIELDS", *fields, "END-OF-FIELDS",
        "TIMESTARTED=Thu Jul 23 18:30:54 EDT 2026",
        "START-OF-DATA",
        *rows_with_implicit_prefix(ids, len(fields), values, "|"),
        "END-OF-DATA",
        "END-OF-FILE",
        "IMATRL",
        f"DATARECORDS={len(ids)}",
        "ENDTIME=Thu Jul 23 18:31:02 EDT 2026",
    ])


def inline_data_attribute() -> None:
    """The DATA= name carried on the START-OF-FIELDS line itself rather than on its own line.

    Both spellings name a section; this one also makes the file read as bulk by content.
    """
    fields = ["PX_LAST", "PX_VOLUME"]
    write("03-fields-inline-data-attribute.dif", [
        "IMAHDR",
        "START-OF-FILE",
        "PROGRAMNAME=getdata",
        "DELIMITER=|",
        "START-OF-FIELDS DATA=PX_LAST_HIST", *fields, "END-OF-FIELDS",
        "START-OF-DATA",
        *rows_with_implicit_prefix(
            ["IBM US Equity", "GE US Equity"], len(fields),
            [["228.41", "3918200"], ["191.06", "5521400"]], "|"),
        "END-OF-DATA",
        "END-OF-FILE",
        "IMATRL",
        "DATARECORDS=2",
    ])


# --------------------------------------------------------------------------------------------
# 10-13 — delimiters. The delimiter is declared per file and never guessed.
# --------------------------------------------------------------------------------------------

def delimiter_samples() -> None:
    fields = ["TICKER", "PX_LAST", "CRNCY"]
    ids = ["BP/ LN Equity", "SHEL LN Equity", "TTE FP Equity"]
    values = [["BP/", "402.15", "GBp"], ["SHEL", "2814.00", "GBp"], ["TTE", "58.91", "EUR"]]

    for name, delim, declared, note in [
        ("10-delimiter-comma.dif", ",", ",", "DELIMITER=,"),
        ("11-delimiter-tab.dif", "\t", "\t", "DELIMITER=<literal tab>"),
        ("12-delimiter-semicolon.dif", ";", ";", "DELIMITER=;"),
    ]:
        write(name, [
            "IMAHDR",
            "START-OF-FILE",
            "PROGRAMNAME=getdata",
            f"DELIMITER={declared}",
            "START-OF-FIELDS", *fields, "END-OF-FIELDS",
            "START-OF-DATA",
            *rows_with_implicit_prefix(ids, len(fields), values, delim),
            "END-OF-DATA",
            "END-OF-FILE",
            "IMATRL",
            f"DATARECORDS={len(ids)}",
        ])

    # No DELIMITER line at all. The file still opens: the parser falls back to '|' and records a
    # warning you can read in the File info panel.
    write("13-delimiter-missing.dif", [
        "IMAHDR",
        "START-OF-FILE",
        "PROGRAMNAME=getdata",
        "START-OF-FIELDS", *fields, "END-OF-FIELDS",
        "START-OF-DATA",
        *rows_with_implicit_prefix(ids, len(fields), values, "|"),
        "END-OF-DATA",
        "END-OF-FILE",
        "IMATRL",
        f"DATARECORDS={len(ids)}",
    ])


# --------------------------------------------------------------------------------------------
# 20-22 — line endings. DIF export reproduces whichever the source used.
# --------------------------------------------------------------------------------------------

def line_ending_samples() -> None:
    fields = ["TICKER", "PX_LAST"]
    ids = ["ES1 Index", "NQ1 Index", "CL1 Comdty"]
    values = [["ES1", "5842.25"], ["NQ1", "20418.75"], ["CL1", "71.44"]]
    body = [
        "INAHDR",
        "FIRMNAME=dl123456",
        "DELIMITER=|",
        "START-OF-FIELDS", *fields, "END-OF-FIELDS",
        "START-OF-DATA",
        *rows_with_implicit_prefix(ids, len(fields), values, "|"),
        "END-OF-DATA",
        "INATRL",
        f"DATARECORDS={len(ids)}",
    ]
    write("20-lineendings-lf.dif", body, eol=LF)
    write("21-lineendings-crlf.dif", body, eol=CRLF)

    # Mixed: CRLF on every other line. The file's line ending is taken from the first one, so this
    # opens as a CRLF file with LF lines inside it — deliberately awkward, and it must still parse.
    mixed = "".join(line + (CRLF if i % 2 == 0 else LF) for i, line in enumerate(body))
    write_raw("22-lineendings-mixed.dif", mixed.encode("utf-8"))


# --------------------------------------------------------------------------------------------
# 30-33 — bulk (multi-section) files
# --------------------------------------------------------------------------------------------

def _section(name: str | None, fields: list[str], ids: list[str], values: list[list[str]]) -> list[str]:
    head = [f"DATA={name}"] if name else []
    return [
        *head,
        "START-OF-FIELDS", *fields, "END-OF-FIELDS",
        "START-OF-DATA",
        *rows_with_implicit_prefix(ids, len(fields), values, "|"),
        "END-OF-DATA",
        f"DATARECORDS={len(ids)}",
    ]


def _bulk_file(name: str, sections: list[list[str]], total: int) -> None:
    write(name, [
        "IMAHDR",
        "START-OF-FILE",
        "FIRMNAME=dl123456",
        "PROGRAMNAME=getdata",
        "DATEFORMAT=yyyymmdd",
        "DELIMITER=|",
        *[line for section in sections for line in section],
        "END-OF-FILE",
        "IMATRL",
        f"DATARECORDS={total}",
    ])


def bulk_named() -> None:
    """Three named sections, each with its own field list — the canonical bulk export."""
    sections = [
        _section("DVD_HIST", ["DECLARED_DATE", "EX_DATE", "DIVIDEND_AMOUNT"],
                 ["AAPL US Equity", "AAPL US Equity", "MSFT US Equity"],
                 [["20260130", "20260209", "0.250000"],
                  ["20251031", "20251110", "0.250000"],
                  ["20260121", "20260213", "0.830000"]]),
        _section("CALL_SCHEDULE", ["CALL_DATE", "CALL_PRICE"],
                 ["EC3478608 Corp", "EC3478608 Corp", "BK0849954 Corp", "BK0849954 Corp"],
                 [["20270601", "104.775000"], ["20280601", "102.388000"],
                  ["20260630", "103.000000"], ["20270630", "101.500000"]]),
        _section("INDX_MEMBERS", ["MEMBER_TICKER_AND_EXCHANGE_CODE", "PERCENT_WEIGHT"],
                 ["SPX Index", "SPX Index", "SPX Index"],
                 [["AAPL UW", "7.412000"], ["MSFT UW", "6.118000"], ["NVDA UW", "6.004000"]]),
    ]
    _bulk_file("30-bulk-named-sections.dif", sections, 10)


def bulk_many() -> None:
    """Eight sections — enough to see the section bar scroll and to feel lazy per-section indexing."""
    random.seed(30313233)
    names = ["DVD_HIST", "CALL_SCHEDULE", "PUT_SCHEDULE", "SINK_SCHEDULE",
             "INDX_MEMBERS", "CHAIN_TICKERS", "CREDIT_RATINGS", "ISSUER_HIERARCHY"]
    sections, total = [], 0
    for i, name in enumerate(names):
        fields = [f"{name}_DATE", f"{name}_VALUE", "SOURCE"]
        count = 3 + i
        ids = [f"SEC{i:02d}{j:03d} Corp" for j in range(count)]
        values = [[f"202{6 + (j % 4)}{(j % 12) + 1:02d}15",
                   f"{random.uniform(10, 500):.6f}",
                   random.choice(["BGN", "BVAL", "CBBT", "TRAC"])] for j in range(count)]
        sections.append(_section(name, fields, ids, values))
        total += count
    _bulk_file("31-bulk-many-sections.dif", sections, total)


def bulk_unnamed() -> None:
    """Sections with no DATA= attribute. Names are synthesized so they're still tellable apart."""
    sections = [
        _section(None, ["PX_OPEN", "PX_HIGH", "PX_LOW", "PX_LAST"],
                 ["UKX Index", "SX5E Index"],
                 [["8214.10", "8251.66", "8198.02", "8240.91"],
                  ["4918.44", "4944.10", "4901.77", "4938.20"]]),
        _section(None, ["FUT_CONT_SIZE", "QUOTE_UNITS"],
                 ["ES1 Index", "NQ1 Index"],
                 [["50", "USD"], ["20", "USD"]]),
    ]
    _bulk_file("32-bulk-unnamed-sections.dif", sections, 4)


def multi_section_plain_name() -> None:
    """Multi-section, but nothing in the name says "bulk" — detection has to come from content."""
    sections = [
        _section("TOP_20_HOLDERS", ["HOLDER_NAME", "PERCENT_OUTSTANDING"],
                 ["AAPL US Equity", "AAPL US Equity"],
                 [["VANGUARD GROUP INC", "8.412000"], ["BLACKROCK INC", "6.887000"]]),
        _section("ERN_ANN_DT_TIME_HIST", ["ANNOUNCEMENT_DT", "ANNOUNCEMENT_TIME"],
                 ["AAPL US Equity", "AAPL US Equity"],
                 [["20260130", "16:30:00"], ["20251030", "16:30:00"]]),
    ]
    _bulk_file("33-multi-section-plain-name.dif", sections, 4)


# --------------------------------------------------------------------------------------------
# 40-41 — column shapes worth pointing the stats, sort and search at
# --------------------------------------------------------------------------------------------

def datatypes_mixed() -> None:
    """One column of each kind the column menu has to cope with.

    Integers, decimals, negatives, a number wide enough to lose precision if anything treats it as
    a float, yyyymmdd dates, free text with the delimiter's near-misses in it, repeated values for
    the distinct-value picker, and blanks.
    """
    fields = ["INT_COL", "DEC_COL", "NEG_COL", "BIG_COL", "DATE_COL", "TEXT_COL", "REPEAT_COL", "BLANK_COL"]
    ids = [f"SEC{i:03d} Equity" for i in range(1, 13)]
    values = [
        ["1", "0.500000", "-12.50", "9007199254740993", "20260115", "Ordinary text", "ALPHA", "x"],
        ["2", "1.250000", "-0.01", "9007199254740994", "20251231", "Comma, inside", "BETA", ""],
        ["3", "10.000000", "-1000.00", "12345678901234567890", "20240229", "Semi; inside", "ALPHA", "x"],
        ["10", "2.750000", "-3.25", "1", "19991231", "Tab\tinside", "GAMMA", ""],
        ["11", "99.999999", "-0.00", "42", "20260701", "Quote \"inside\"", "ALPHA", "x"],
        ["100", "0.000001", "-99999.99", "1000000000000", "20260228", "Trailing space ", "BETA", ""],
        ["101", "3.141593", "-2.71", "999999999999999999", "20200101", " Leading space", "GAMMA", "x"],
        ["2", "1.250000", "-0.01", "0", "20260115", "Ordinary text", "ALPHA", ""],
        ["-5", "-0.500000", "0.00", "-9007199254740993", "20301231", "Negative id row", "DELTA", "x"],
        ["0", "0.000000", "0.00", "0", "20260101", "", "BETA", ""],
        ["999", "1234567.890123", "-0.000001", "7", "20261231", "Very long value " + "y" * 120, "ALPHA", "x"],
        ["", "", "", "", "", "", "DELTA", ""],
    ]
    write("40-datatypes-mixed.dif", [
        "IMAHDR",
        "START-OF-FILE",
        "PROGRAMNAME=getdata",
        "DATEFORMAT=yyyymmdd",
        "DELIMITER=|",
        "START-OF-FIELDS", *fields, "END-OF-FIELDS",
        "START-OF-DATA",
        *rows_with_implicit_prefix(ids, len(fields), values, "|"),
        "END-OF-DATA",
        "END-OF-FILE",
        "IMATRL",
        f"DATARECORDS={len(ids)}",
    ])


def unicode_and_blanks() -> None:
    """Non-ASCII security names and field values, plus error rows with everything blank.

    Worth opening with case-sensitive search on and off: the raw-byte prefilter only handles ASCII
    case folding, so these rows prove the decoded path still matches.
    """
    fields = ["LONG_COMP_NAME", "CNTRY_OF_DOMICILE", "CRNCY"]
    ids = ["7203 JT Equity", "005930 KS Equity", "MC FP Equity", "NESN SW Equity",
           "ROSN RM Equity", "BADSEC Equity"]
    values = [
        ["トヨタ自動車株式会社", "JP", "JPY"],
        ["삼성전자주식회사", "KR", "KRW"],
        ["LVMH Moët Hennessy · Louis Vuitton", "FR", "EUR"],
        ["Nestlé S.A.", "CH", "CHF"],
        ["ПАО «Роснефть»", "RU", "RUB"],
        ["", "", ""],
    ]
    rows = rows_with_implicit_prefix(ids[:-1], len(fields), values[:-1], "|")
    rows.append("|".join(["BADSEC Equity", "10", "3", "", "", ""]))  # _ERR=10, no values
    write("41-unicode-and-blanks.dif", [
        "IMAHDR",
        "START-OF-FILE",
        "PROGRAMNAME=getdata",
        "ENCODING=UTF-8",
        "DELIMITER=|",
        "START-OF-FIELDS", *fields, "END-OF-FIELDS",
        "START-OF-DATA", *rows, "END-OF-DATA",
        "END-OF-FILE",
        "IMATRL",
        f"DATARECORDS={len(ids)}",
    ])


# --------------------------------------------------------------------------------------------
# 50 — something big enough to feel
# --------------------------------------------------------------------------------------------

def perf_sample(rows: int) -> None:
    """A file large enough that paging, search and sort are doing real work.

    25,000 rows is the committed size — big enough to notice, small enough to keep in git. Pass
    --perf-rows to generate something in the millions locally; the app is built for 2 GB files and
    this is where you'd see it.
    """
    random.seed(50)
    fields = ["TICKER", "PX_LAST", "PX_VOLUME", "CRNCY", "CNTRY", "GICS_SECTOR", "MATURITY", "YLD_YTM_MID"]
    currencies = ["USD", "EUR", "GBp", "JPY", "CHF", "HKD", "AUD", "CAD"]
    countries = ["US", "DE", "GB", "JP", "CH", "HK", "AU", "CA"]
    sectors = ["Financials", "Energy", "Health Care", "Info Tech", "Utilities",
               "Materials", "Industrials", "Cons Staples"]
    lines = []
    for i in range(rows):
        c = i % len(currencies)
        lines.append("|".join([
            f"SEC{i:07d} Equity", "0", str(len(fields)),
            f"TCK{i % 5000:04d}",
            f"{random.uniform(0.5, 9999):.4f}",
            str(random.randint(1000, 90_000_000)),
            currencies[c], countries[c], sectors[i % len(sectors)],
            f"20{random.randint(26, 45)}{random.randint(1, 12):02d}{random.randint(1, 28):02d}",
            f"{random.uniform(-1, 14):.6f}",
        ]))
    write(f"50-perf-{rows // 1000}k-rows.dif", [
        "IMAHDR",
        "START-OF-FILE",
        "FIRMNAME=dl123456",
        "PROGRAMNAME=getdata",
        "DATEFORMAT=yyyymmdd",
        "DELIMITER=|",
        "START-OF-FIELDS", *fields, "END-OF-FIELDS",
        "START-OF-DATA", *lines, "END-OF-DATA",
        "END-OF-FILE",
        "IMATRL",
        f"DATARECORDS={rows}",
    ])


# --------------------------------------------------------------------------------------------
# 60-66 — malformed files. None of these may crash the app; all report what's wrong instead.
# --------------------------------------------------------------------------------------------

def edge_cases() -> None:
    fields = ["TICKER", "PX_LAST"]
    ids = ["AAPL US Equity", "MSFT US Equity", "IBM US Equity"]
    values = [["AAPL", "241.84"], ["MSFT", "428.02"], ["IBM", "228.41"]]
    rows = rows_with_implicit_prefix(ids, len(fields), values, "|")
    head = ["IMAHDR", "START-OF-FILE", "PROGRAMNAME=getdata", "DELIMITER=|",
            "START-OF-FIELDS", *fields, "END-OF-FIELDS", "START-OF-DATA"]

    # A section that returned nothing. Valid, not an error — the grid is simply empty.
    write("60-edge-empty-data.dif", [*head, "END-OF-DATA", "END-OF-FILE", "IMATRL", "DATARECORDS=0"])

    # The trailer's count disagrees with the rows present. The rows win; the mismatch is reported.
    write("61-edge-datarecords-mismatch.dif",
          [*head, *rows, "END-OF-DATA", "END-OF-FILE", "IMATRL", "DATARECORDS=99"])

    # Rows with too few and too many values for the declared field list. Neither is dropped.
    write("62-edge-ragged-rows.dif", [
        *head,
        rows[0],
        "SHORT US Equity|0|2|SHORT",
        "LONG US Equity|0|2|LONG|1.00|extra|values|here",
        rows[2],
        "END-OF-DATA", "END-OF-FILE", "IMATRL", "DATARECORDS=4",
    ])

    # No END-OF-DATA: the data section is treated as running to the end of the file.
    write("63-edge-missing-end-of-data.dif", [*head, *rows])

    # Cut off mid-row, as a transfer that died would leave it.
    truncated = "".join(line + LF for line in [*head, rows[0], rows[1]]) + "IBM US Equity|0|2|IBM|228."
    write_raw("64-edge-truncated-mid-row.dif", truncated.encode("utf-8"))

    # Markers carrying trailing content. These are still markers, not data.
    write("65-edge-marker-trailing-content.dif", [
        "IMAHDR", "START-OF-FILE", "PROGRAMNAME=getdata", "DELIMITER=|",
        "START-OF-FIELDS", *fields, "END-OF-FIELDS",
        "START-OF-DATA",
        *rows,
        "END-OF-DATA    (3 records returned)",
        "END-OF-FILE",
        "IMATRL",
        "DATARECORDS=3",
    ])

    # Not a DIF file at all. Must be refused with a diagnostic, never a crash.
    random.seed(66)
    write_raw("66-edge-not-a-dif-file.bin", bytes(random.randrange(256) for _ in range(4096)))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--perf-rows", type=int, default=25_000,
                        help="rows in the performance sample (default: 25000, the committed size)")
    args = parser.parse_args()

    classic_inahdr()
    getdata_imahdr()
    inline_data_attribute()
    delimiter_samples()
    line_ending_samples()
    bulk_named()
    bulk_many()
    bulk_unnamed()
    multi_section_plain_name()
    datatypes_mixed()
    unicode_and_blanks()
    perf_sample(args.perf_rows)
    edge_cases()


if __name__ == "__main__":
    main()
