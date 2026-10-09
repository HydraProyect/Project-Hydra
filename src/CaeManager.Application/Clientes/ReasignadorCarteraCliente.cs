using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Notificaciones;

namespace CaeManager.Application.Clientes;

/// <summary>
/// Pasa la <b>referencia</b> de un Cliente empresarial a otro Gestor CAE (o se la quita)
/// <b>sin guardar</b>: deja en el contexto la proyección <c>Empresa</c> y los avisos, para que
/// el Command que lo usa los confirme en su propio guardado. Lo comparten
/// <c>ReasignarEjecutivoClienteCommand</c> (un Cliente empresarial) y
/// <c>DesactivarGestorCaeConCarteraCommand</c> (todos los suyos y la baja, en una transacción),
/// para que las dos vías apliquen las mismas reglas.
///
/// <para>
/// <b>La referencia no es cartera</b> (D-7, 2026-10-02): <c>Empresa.EjecutivoUsuarioId</c> solo
/// alimenta el enrutado de WhatsApp, los avisos y la columna de la lista. Reasignarla no abre ni
/// cierra ninguna Asignación de Cartera y no cambia lo que nadie alcanza: la cartera de un
/// Gestor CAE es siempre el Tenant entero.
/// </para>
///
/// <para>
/// <b>No autoriza el rol de quien reasigna</b>: eso lo decide cada Command. Sí
/// comprueba que el Cliente empresarial esté en su alcance y que el destino pueda
/// llevar la referencia (<see cref="ReglaDestinoCarteraCliente"/>).
/// </para>
/// </summary>
public class ReasignadorCarteraCliente(
    IEmpresaRepository empresaRepositorio,
    IConfiguracionIaDocumentoClienteRepository configuracionIaRepositorio,
    INotificacionUsuarioRepository notificacionRepositorio,
    ICurrentUserService currentUserService,
    IAlcanceDatosService alcanceDatos,
    IDirectorioDestinosCartera directorioDestinos,
    IBloqueoCarteraUsuario bloqueoCartera)
{
    public static readonly Error ClienteNoEncontrado = Error.Crear("Cliente.NoEncontrado", "No encontramos este Cliente.");

    /// <returns>
    /// <c>true</c> si dejó cambios que guardar; <c>false</c> si el Cliente empresarial ya
    /// tenía a ese Gestor CAE como referencia y no hay nada que hacer.
    /// </returns>
    public async Task<Result<bool>> ReasignarAsync(
        Guid clienteId, Guid? nuevoGestorId, CancellationToken cancellationToken)
    {
        var empresa = await empresaRepositorio.ObtenerPorIdAsync(clienteId, cancellationToken);
        if (empresa is null || !await alcanceDatos.ClienteVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo<bool>(ClienteNoEncontrado);

        var gestorAnteriorId = empresa.EjecutivoUsuarioId;
        if (gestorAnteriorId == nuevoGestorId)
            return Result.Exito(false);

        // Antes de validar el destino: si una desactivación con traspaso está en curso sobre
        // cualquiera de las dos cuentas, se espera a que termine y se valida lo que dejó
        // (IBloqueoCarteraUsuario). Exige estar dentro de una transacción.
        await bloqueoCartera.BloquearCompartidoAsync(
            new[] { gestorAnteriorId, nuevoGestorId }.OfType<Guid>().ToList(), cancellationToken);

        if (nuevoGestorId is { } destinoId)
        {
            var destinoValido = await ReglaDestinoCarteraCliente.ValidarAsync(
                destinoId, directorioDestinos, currentUserService, cancellationToken);
            if (destinoValido.EsFallido)
                return Result.Fallo<bool>(destinoValido.Error);
        }

        await AplicarCambioDeGestorAsync(empresa, gestorAnteriorId, nuevoGestorId, cancellationToken);

        return Result.Exito(true);
    }

    private async Task AplicarCambioDeGestorAsync(
        Empresa empresa, Guid? gestorAnteriorId, Guid? nuevoGestorId, CancellationToken cancellationToken)
    {
        empresa.AsignarEjecutivo(nuevoGestorId);

        if (gestorAnteriorId is not null)
            notificacionRepositorio.Agregar(new NotificacionUsuario(
                gestorAnteriorId.Value,
                "Cambio en tus Clientes de referencia",
                $"Ya no eres el Gestor CAE de referencia del Cliente \"{empresa.RazonSocial}\". Tu cartera no cambia."));

        if (nuevoGestorId is not null)
        {
            notificacionRepositorio.Agregar(new NotificacionUsuario(
                nuevoGestorId.Value,
                "Cambio en tus Clientes de referencia",
                $"Eres ahora el Gestor CAE de referencia del Cliente \"{empresa.RazonSocial}\". Tu cartera no cambia."));

            var tiposSinLecturaIa = await configuracionIaRepositorio.ObtenerNombresTiposDocumentoSinLecturaIaAsync(empresa.Id, cancellationToken);
            if (tiposSinLecturaIa.Count > 0)
                notificacionRepositorio.Agregar(new NotificacionUsuario(
                    nuevoGestorId.Value,
                    "Lectura automática por IA desactivada",
                    $"El Cliente \"{empresa.RazonSocial}\" tiene la lectura automática por IA desactivada para: {string.Join(", ", tiposSinLecturaIa)}.",
                    urlAccion: $"/clientes/{empresa.Id}/lectura-ia",
                    textoAccion: "Gestionar"));
        }
    }
}
