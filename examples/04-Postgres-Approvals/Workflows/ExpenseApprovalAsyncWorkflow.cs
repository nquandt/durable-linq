using DurableLinq;
using PostgresApprovals.Models;
using PostgresApprovals.Services;

namespace PostgresApprovals.Workflows;

/// <summary>
/// The same approval as <see cref="ExpenseApprovalWorkflow"/>, written as an <c>async Durable&lt;T&gt;</c> method.
/// The branch is a plain <c>if</c> on the recorded policy value, so no <c>if</c> entry is written; the
/// query version records one through <c>Durable.If</c>. That is the one place the two logs differ, so do not
/// switch styles while an approval is in flight. The timeout is handled with <c>Recover</c> on the durable
/// before it is awaited, which records the failure so a replay takes the same path.
/// </summary>
public sealed class ExpenseApprovalAsyncWorkflow(INotifier notifier) : IWorkflow<Expense, Decision>
{
    public static string Name => "expense-approval";
    public static int Version => 1;

    public async Durable<Decision> Handle(Expense e)
    {
        var policy = await Durable.Step("check-policy", () => e.Amount < 500 ? "auto" : "manager");

        Decision decision;
        if (policy == "auto")
        {
            decision = new Decision(true, "policy", "under limit");
        }
        else
        {
            decision = await Durable.WaitFor<Review>("manager-decision", timeout: TimeSpan.FromMinutes(2))
                .Select(review => new Decision(review.Approved, review.By, review.Comment ?? ""))
                .Recover(WorkflowError.TimeoutCode, _ => Durable.Step("escalate", () => new Decision(false, "system", "no decision within 2 minutes")));
        }

        await Durable.Step("notify", () => notifier.NotifyAsync($"{e.Employee}: {(decision.Approved ? "approved" : "rejected")} by {decision.DecidedBy}"));

        return decision;
    }
}
