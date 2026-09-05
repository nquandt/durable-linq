using DurableLinq;
using PostgresApprovals.Models;
using PostgresApprovals.Services;

namespace PostgresApprovals.Workflows;

/// <summary>
/// The expense approval workflow. Takes no external dependencies besides the notifier; the hosting
/// package creates one instance per run from that run's DI scope.
/// </summary>
public sealed class ExpenseApprovalWorkflow(INotifier notifier) : IWorkflow<Expense, Decision>
{
    public static string Name => "expense-approval";
    public static int Version => 1;

    public Durable<Decision> Handle(Expense e) =>
        from policy in Durable.Step("check-policy", () => e.Amount < 500 ? "auto" : "manager")
        from decision in Durable.If(policy == "auto",
            Durable.Return(new Decision(true, "policy", "under limit")),
            (from review in Durable.WaitFor<Review>("manager-decision", timeout: TimeSpan.FromMinutes(2))
             select new Decision(review.Approved, review.By, review.Comment ?? ""))
                .Recover(WorkflowError.TimeoutCode, _ => Durable.Step("escalate", () => new Decision(false, "system", "no decision within 2 minutes"))))
        from _ in Durable.Step("notify", () => notifier.NotifyAsync($"{e.Employee}: {(decision.Approved ? "approved" : "rejected")} by {decision.DecidedBy}"))
        select decision;
}
