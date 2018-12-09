namespace GeekLearning.Storage
{
    using System;

    public interface ISharedIpAddressOrRange
    {
        string Address { get; }
        string MinimumAddress { get; }
        string MaximumAddress { get; }
        bool IsSingleAddress { get; }
    }
}
