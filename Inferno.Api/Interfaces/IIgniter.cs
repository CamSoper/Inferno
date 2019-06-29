namespace Inferno.Api.Interfaces
{
    public interface IIgniter
    {
         void On();
         void Off();
         bool IsOn { get; }
    }
}