using AnisePOS.Api.Domain;
using AnisePOS.Api.Endpoints;

var accepted = new HashSet<(OrderStatus, OrderStatus)>
{
    (OrderStatus.Draft, OrderStatus.Preparing),
    (OrderStatus.Placed, OrderStatus.Preparing),
    (OrderStatus.Accepted, OrderStatus.Preparing),
    (OrderStatus.Preparing, OrderStatus.Ready),
    (OrderStatus.Ready, OrderStatus.Completed)
};
var statuses = Enum.GetValues<OrderStatus>().Append((OrderStatus)999).ToArray();
var checks = 0;
foreach (var from in statuses)
foreach (var to in statuses)
{
    if (OrderQueueEndpoints.CanTransition(from, to) != accepted.Contains((from, to)))
        throw new Exception($"Unexpected transition permission: {from} -> {to}");
    checks++;
}
Console.WriteLine($"Passed {checks} transition checks, including skipped stages, repeats, terminal states and invalid status values.");
