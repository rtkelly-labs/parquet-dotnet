// Commit subjects in the change stack, and pull request titles (Lake.NET docs/lake-parquet/changes.md): one change per
// commit, `lake(NNNN): title`. commitlint skips `fixup!`/`squash!` commits by default, which the next
// rebaseline folds into their change. CI runs it on the stack and on every pull request's title.
export default {
    parserPreset: {
        parserOpts: {
            headerPattern: /^(lake)\((\d{4})\): (\S.*)$/,
            headerCorrespondence: ["type", "scope", "subject"],
        },
    },
    rules: {
        "type-empty": [2, "never"],
        "type-enum": [2, "always", ["lake"]],
        "scope-empty": [2, "never"],
        "subject-empty": [2, "never"],
        "header-max-length": [2, "always", 120],
    },
    helpUrl: "Lake.NET docs/lake-parquet/changes.md",
};
