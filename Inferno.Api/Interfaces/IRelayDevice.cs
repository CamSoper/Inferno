namespace Inferno.Api.Interfaces
{
    public interface IRelayDevice
    {
         void On();
         void Off();
         bool IsOn { get; }
    }
}