namespace GeekLearning.Storage
{
    using System;

    public class SharedIpAddressOrRange : ISharedIpAddressOrRange
    {
        public string Address { get; }
        public string MinimumAddress { get; }
        public string MaximumAddress { get; }
        public bool IsSingleAddress { get; }

        public SharedIpAddressOrRange(string address)
        {
            Address = address;
            IsSingleAddress = true;
        }
        public SharedIpAddressOrRange(string minimum, string maximum)
        {
            MinimumAddress = minimum;
            MaximumAddress = maximum;
            IsSingleAddress = false;
        }
    }
}
