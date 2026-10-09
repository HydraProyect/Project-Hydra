namespace CaeManager.Web.Components.DesignSystem;

/// <summary>Tono de una <c>BandaAccion</c>: lo que pide atención ya o lo que la pedirá pronto.</summary>
public enum TonoBanda
{
    Peligro,
    Advertencia,

    /// <summary>Informa de un estado sin urgencia («Visita cancelada»): no pide ninguna acción.</summary>
    Neutro
}
