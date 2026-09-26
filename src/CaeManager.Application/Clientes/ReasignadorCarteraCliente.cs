using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Notificaciones;

namespace CaeManager.Application.Clientes;

/// <summary>
/// Pasa un Cliente empresarial a otro Gestor CAE (o se lo quita) <b>sin guardar</b>:
/// deja en el contexto la proyección <c>Empresa</c>, la Asignación de Cartera y los
/// avisos, para que el Command que lo usa los confirme en su propio guardado. Lo
/// comparten <c>ReasignarEjecutivoClienteCommand</c> (un Cliente empresarial) y
/// <c>DesactivarGestorCaeConCarteraCommand</c> (toda la cartera y la baja, en una
/// transacción), para que las dos vías apliquen las mismas reglas.
///
/// <para>
/// <b>No autoriza el rol de quien reasigna</b>: eso lo decide cada Command. Sí
/// comprueba que el Cliente empresarial esté en su alcance y que el destino pueda
/// llevar la cartera (<see cref="ReglaDestinoCarteraCliente"/>).
/// </para>
/// </summary>
public class ReasignadorCarteraCliente(
    IEmpresaRepository empresaRepositorio,
    IConfiguracionIaDocumentoClienteRepository configuracionIaRepositorio,
    INotificacionUsuarioRepository notificacionRepositorio,
    ICurrentUserService currentUserService,
    IAlcanceDatosService alcanceDatos,
    IAsignacionesOperativasWriter asignacionesWriter,
    IDirectorioDestinosCartera directorioDestinos)
{
    public static readonly Error ClienteNoEncontrado = Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente.");

    /// <param name="alinearCartera">
    /// Aunque la proyección <c>Empresa</c> ya apunte al destino, cerrar las Asignaciones de
    /// Cartera de otros y abrir la del destino. Lo usa el traspaso de cartera al desactivar,
    /// que parte de las Asignaciones de Cartera: si la proyección y la cartera divergen, sin
    /// esto el Cliente empresarial se quedaba en la cartera de quien se desactiva
    /// (revisión puente del incremento B).
    /// </param>
    /// <returns>
    /// <c>true</c> si dejó cambios que guardar; <c>false</c> si el Cliente empresarial ya
    /// era de ese Gestor CAE y no hay nada que hacer.
    /// </returns>
    public async Task<Result<bool>> ReasignarAsync(
        Guid clienteId, Guid? nuevoGestorId, CancellationToken cancellationToken, bool alinearCartera = false)
    {
        var empresa = await empresaRepositorio.ObtenerPorIdAsync(clienteId, cancellationToken);
        if (empresa is null || !await alcanceDatos.ClienteVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo<bool>(ClienteNoEncontrado);

        var gestorAnteriorId = empresa.EjecutivoUsuarioId;
        var cambiaDeGestor = gestorAnteriorId != nuevoGestorId;
        if (!cambiaDeGestor && !alinearCartera)
            return Result.Exito(false);

        if (nuevoGestorId is { } destinoId)
        {
            var destinoValido = await ReglaDestinoCarteraCliente.ValidarAsync(
                destinoId, directorioDestinos, currentUserService, cancellationToken);
            if (destinoValido.EsFallido)
                return Result.Fallo<bool>(destinoValido.Error);
        }

        if (cambiaDeGestor)
            await AplicarCambioDeGestorAsync(empresa, gestorAnteriorId, nuevoGestorId, cancellationToken);

        // Doble escritura: la cartera nueva entra en el mismo guardado que la
        // proyección Empresa, así que o se guardan las dos o ninguna. La
        // proyección sigue siendo la autoritativa durante F1.
        await asignacionesWriter.ReasignarCarteraClienteAsync(empresa.Id, nuevoGestorId, cancellationToken);

        return Result.Exito(true);
    }

    private async Task AplicarCambioDeGestorAsync(
        Empresa empresa, Guid? gestorAnteriorId, Guid? nuevoGestorId, CancellationToken cancellationToken)
    {
        empresa.AsignarEjecutivo(nuevoGestorId);

        if (gestorAnteriorId is not null)
            notificacionRepositorio.Agregar(new NotificacionUsuario(
                gestorAnteriorId.Value,
                "Cambio en tu cartera de clientes",
                $"Se te ha quitado el cliente \"{empresa.RazonSocial}\" de tu cartera."));

        if (nuevoGestorId is not null)
        {
            notificacionRepositorio.Agregar(new NotificacionUsuario(
                nuevoGestorId.Value,
                "Cambio en tu cartera de clientes",
                $"Se te ha asignado el cliente \"{empresa.RazonSocial}\" en tu cartera."));

            var tiposSinLecturaIa = await configuracionIaRepositorio.ObtenerNombresTiposDocumentoSinLecturaIaAsync(empresa.Id, cancellationToken);
            if (tiposSinLecturaIa.Count > 0)
                notificacionRepositorio.Agregar(new NotificacionUsuario(
                    nuevoGestorId.Value,
                    "Lectura automática por IA desactivada",
                    $"El cliente \"{empresa.RazonSocial}\" tiene la lectura automática por IA desactivada para: {string.Join(", ", tiposSinLecturaIa)}.",
                    urlAccion: $"/clientes/{empresa.Id}/lectura-ia",
                    textoAccion: "Gestionar"));
        }
    }
}
