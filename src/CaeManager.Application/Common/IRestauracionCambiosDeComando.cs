namespace CaeManager.Application.Common;

/// <summary>Captura el estado rastreado para restaurar lo pendiente si falla un comando.</summary>
public interface IRestauracionCambiosDeComando
{
    IInstantaneaCambiosDeComando CapturarRestauracion();
}

/// <summary>Restaura lo pendiente y libera la observación de guardados del comando.</summary>
public interface IInstantaneaCambiosDeComando : IDisposable
{
    void Restaurar();
}
