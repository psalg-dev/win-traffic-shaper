using ShapeTraffic.Core.Models;

namespace ShapeTraffic.App.ViewModels;

public sealed record TimeRangeChoice(TimeRangeOption Value, string Label)
{
	public override string ToString() => Label;
}