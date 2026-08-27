namespace ReleaseTool.Api.Adf;

/// <summary>The columns this tool writes to. Everything else is left alone.</summary>
public enum ApprovalColumn
{
    DeveloperAssigned,
    RequestedBy,
    PrApprovedBy,
    PrApprovedStatus,
    MergedToDeploymentBranch
}

public static class ApprovalColumns
{
    /// <summary>
    /// Header text per column, longest first. "PR Approved Status" has to be
    /// matched before "PR Approved By", or a loose contains-match claims the
    /// wrong column.
    /// </summary>
    public static readonly (ApprovalColumn Column, string Header)[] Headers =
    [
        (ApprovalColumn.MergedToDeploymentBranch, "merged to deployment branch"),
        (ApprovalColumn.PrApprovedStatus, "pr approved status"),
        (ApprovalColumn.DeveloperAssigned, "developer assigned"),
        (ApprovalColumn.PrApprovedBy, "pr approved by"),
        (ApprovalColumn.RequestedBy, "requested by")
    ];

    /// <summary>Value written into the status columns when a PR approval is recorded.</summary>
    public static string StatusTextFor(ApprovalColumn column) => column switch
    {
        ApprovalColumn.PrApprovedStatus => "Approved",
        ApprovalColumn.MergedToDeploymentBranch => "Merged",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Not a status column.")
    };
}
