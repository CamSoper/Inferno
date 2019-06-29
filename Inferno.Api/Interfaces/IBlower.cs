namespace Inferno.Api.Interfaces
{
    public interface IBlower
    {
         void On();
         void Off();
         bool IsOn { get; }
    }
}