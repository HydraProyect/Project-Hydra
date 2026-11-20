using CaeManager.Application.Common;

// En un área de Outbound (Application.Subcontratas) para que PeticionDeOutbound los clasifique como de Outbound; solo
// los usa GuardaPorServicioBehaviorTests.
namespace CaeManager.Application.Subcontratas.PruebasDeGuarda;

/// <summary>Un comando en un área de Outbound, solo para la guarda.</summary>
public sealed record ComandoDeOutbound : ICommand;

/// <summary>Un comando de autoservicio en un área de Outbound, solo para la guarda.</summary>
public sealed record ComandoDeOutboundDeAutoservicio : ICommand, IComandoDeAutoservicio;
