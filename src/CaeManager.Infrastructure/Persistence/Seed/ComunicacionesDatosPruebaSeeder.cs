using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra la bandeja de correo compartida con datos de prueba en vez de
/// ingesta real de Microsoft Graph — es la simplificación explícita de la
/// primera pieza del módulo Comunicaciones (ver
/// ARQUITECTURA-INTEGRACIONES.md § 12.6). Reutiliza los Clientes ya
/// sembrados por <see cref="DatosPruebaSeeder"/> (que debe ejecutarse antes)
/// en vez de crear datos maestros propios.
///
/// Mismo criterio de activación que <see cref="DatosPruebaSeeder"/>: gateado
/// por <c>DatosPrueba:Activo</c> e idempotente (si ya hay alguna
/// Conversacion, no vuelve a sembrar).
/// </summary>
public static class ComunicacionesDatosPruebaSeeder
{
    private const string RemitenteSimuladoTenant = "equipo-cae@buzon-simulado.local";

    private static readonly string[] AsuntosConversacion =
    [
        "Documentación pendiente de renovar", "Consulta sobre acceso al centro", "Alta de trabajador nuevo",
        "Vencimiento de formación PRL", "Duda sobre coordinación de actividades", "Incidencia con el acceso a planta",
        "Solicitud de certificado de empresa", "Revisión de evaluación de riesgos", "Confirmación de visita programada",
        "Baja de trabajador en el centro", "Actualización de datos de contacto", "Consulta sobre subcontrata autorizada",
        "Recordatorio de vencimiento de seguro", "Petición de copia de contrato", "Duda sobre requisito documental"
    ];

    private static readonly string[] FragmentosCuerpoEntrante =
    [
        "Buenos días, os escribo porque necesitamos revisar la documentación antes de la próxima visita.",
        "¿Podríais confirmarme si ya tenéis el certificado actualizado de este trabajador?",
        "Adjunto la información solicitada, quedamos a la espera de vuestra confirmación.",
        "Tenemos previsto el acceso al centro la semana que viene, ¿está todo en regla?",
        "Necesitamos dar de alta a un nuevo trabajador antes del lunes, ¿qué documentación hace falta?"
    ];

    private static readonly string[] FragmentosCuerpoSaliente =
    [
        "Buenos días, hemos revisado la documentación y está todo correcto.",
        "Gracias por el aviso, actualizamos el expediente y os confirmamos en breve.",
        "Adjuntamos el certificado solicitado, cualquier duda quedamos a vuestra disposición.",
        "Confirmado el acceso, el trabajador ya figura como autorizado en el centro.",
        "Hemos detectado un documento próximo a vencer, os lo señalamos para que lo renovéis con tiempo."
    ];

    private static readonly string[] TitulosMacro =
    [
        "Confirmación de documentación en regla", "Solicitud de renovación de certificado", "Aviso de vencimiento próximo",
        "Confirmación de alta de trabajador", "Recordatorio de visita programada", "Solicitud de documentación pendiente",
        "Aviso de acceso autorizado", "Cierre de incidencia resuelta", "Bienvenida a nuevo contacto", "Petición de datos de contacto"
    ];

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo"))
        {
            logger.LogInformation("DatosPrueba:Activo no está activado — no se siembra la bandeja de Comunicaciones.");
            return;
        }

        if (await dbContext.Conversaciones.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Ya hay Conversaciones sembradas — se omite la siembra de Comunicaciones.");
            return;
        }

        var clientes = await dbContext.Clientes.OrderBy(c => c.RazonSocial).Take(40).ToListAsync(cancellationToken);
        if (clientes.Count == 0)
        {
            logger.LogInformation("No hay Clientes sembrados todavía — se omite la siembra de Comunicaciones (ejecuta DatosPruebaSeeder primero).");
            return;
        }

        var trabajadores = await dbContext.Trabajadores.Take(60).ToListAsync(cancellationToken);
        var empresas = await dbContext.Empresas.Take(40).ToListAsync(cancellationToken);
        var subcontratas = await dbContext.Subcontratas.Take(40).ToListAsync(cancellationToken);
        var centros = await dbContext.Centros.Take(40).ToListAsync(cancellationToken);

        var gestoresPrueba = (await userManager.GetUsersInRoleAsync(Roles.GestorCae)).ToList();

        var aleatorio = new Random(20260730);
        var ahora = DateTime.UtcNow;

        const int totalConversaciones = 38;
        const int conversacionesTriage = 5;

        for (var i = 0; i < totalConversaciones; i++)
        {
            var esTriage = i < conversacionesTriage;
            var cliente = esTriage ? null : ElementoAleatorio(aleatorio, clientes);
            var asunto = $"{ElementoAleatorio(aleatorio, AsuntosConversacion)} — {(cliente?.RazonSocial ?? "remitente sin identificar")}";
            var etiquetas = aleatorio.Next(4) == 0 ? "urgente" : null;

            var conversacion = new Conversacion(asunto, cliente?.Id, etiquetas);

            var totalMensajes = aleatorio.Next(2, 6);
            var fechaMensaje = ahora.AddDays(-aleatorio.Next(1, 45)).AddHours(-aleatorio.Next(0, 23));
            var emailExterno = cliente is not null ? EmailSimuladoDeCliente(cliente) : "contacto@dominio-desconocido.com";

            for (var m = 0; m < totalMensajes; m++)
            {
                fechaMensaje = fechaMensaje.AddHours(aleatorio.Next(2, 30));
                if (fechaMensaje > ahora) fechaMensaje = ahora;

                var esEntrante = m % 2 == 0;
                conversacion.AgregarMensaje(
                    esEntrante ? DireccionMensaje.Entrante : DireccionMensaje.Saliente,
                    esEntrante ? emailExterno : RemitenteSimuladoTenant,
                    $"<p>{ElementoAleatorio(aleatorio, esEntrante ? FragmentosCuerpoEntrante : FragmentosCuerpoSaliente)}</p>",
                    fechaMensaje);
            }

            conversacion.AgregarParticipante(emailExterno, RolParticipante.De, TipoParticipanteOrigen.UsuarioCliente);
            conversacion.AgregarParticipante(RemitenteSimuladoTenant, RolParticipante.Para, TipoParticipanteOrigen.Desconocido);

            if (aleatorio.Next(2) == 0)
                AgregarParticipanteRelacionadoAleatorio(conversacion, aleatorio, trabajadores, empresas, subcontratas, centros);

            var estado = aleatorio.Next(100) switch
            {
                < 45 => EstadoConversacion.Abierta,
                < 75 => EstadoConversacion.Pendiente,
                < 92 => EstadoConversacion.Resuelta,
                _ => EstadoConversacion.Cerrada
            };
            if (estado != EstadoConversacion.Abierta)
                conversacion.CambiarEstado(estado);

            if (!esTriage && gestoresPrueba.Count > 0 && aleatorio.Next(3) != 0)
                conversacion.Asignar(ElementoAleatorio(aleatorio, gestoresPrueba).Id);

            dbContext.Conversaciones.Add(conversacion);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var macros = new List<MacroRespuesta>();
        for (var i = 0; i < TitulosMacro.Length; i++)
        {
            var esGenerica = i % 3 != 0;
            var clienteId = esGenerica ? (Guid?)null : ElementoAleatorio(aleatorio, clientes).Id;
            macros.Add(new MacroRespuesta(TitulosMacro[i], $"<p>{TitulosMacro[i]}. Quedamos a vuestra disposición para cualquier duda.</p>", clienteId));
        }
        dbContext.MacrosRespuesta.AddRange(macros);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Comunicaciones sembradas: {Conversaciones} conversaciones ({Triage} sin cliente asignado), {Macros} macros de respuesta.",
            totalConversaciones, conversacionesTriage, macros.Count);
    }

    private static void AgregarParticipanteRelacionadoAleatorio(
        Conversacion conversacion, Random aleatorio,
        IReadOnlyList<Trabajador> trabajadores,
        IReadOnlyList<Empresa> empresas,
        IReadOnlyList<Subcontrata> subcontratas,
        IReadOnlyList<Centro> centros)
    {
        switch (aleatorio.Next(4))
        {
            case 0 when trabajadores.Count > 0:
                var trabajador = ElementoAleatorio(aleatorio, trabajadores);
                conversacion.AgregarParticipante($"{trabajador.NombreCompleto.Replace(" ", ".").ToLowerInvariant()}@trabajador.local", RolParticipante.Cc, TipoParticipanteOrigen.Trabajador, trabajador.Id);
                break;
            case 1 when empresas.Count > 0:
                var empresa = ElementoAleatorio(aleatorio, empresas);
                conversacion.AgregarParticipante($"contacto@{Slug(empresa.RazonSocial)}.com", RolParticipante.Cc, TipoParticipanteOrigen.Empresa, empresa.Id);
                break;
            case 2 when subcontratas.Count > 0:
                var subcontrata = ElementoAleatorio(aleatorio, subcontratas);
                conversacion.AgregarParticipante($"contacto@{Slug(subcontrata.RazonSocial)}.com", RolParticipante.Cc, TipoParticipanteOrigen.Subcontrata, subcontrata.Id);
                break;
            case 3 when centros.Count > 0:
                var centro = ElementoAleatorio(aleatorio, centros);
                conversacion.AgregarParticipante($"{Slug(centro.Nombre)}@centro.local", RolParticipante.Cc, TipoParticipanteOrigen.Centro, centro.Id);
                break;
            default:
                conversacion.AgregarParticipante("desconocido@sin-resolver.local", RolParticipante.Cc, TipoParticipanteOrigen.Desconocido);
                break;
        }
    }

    private static string EmailSimuladoDeCliente(Cliente cliente) => $"contacto@{Slug(cliente.RazonSocial)}.com";

    private static string Slug(string texto)
    {
        var normalizado = new string(texto.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return normalizado.Length > 20 ? normalizado[..20] : normalizado;
    }

    private static T ElementoAleatorio<T>(Random aleatorio, IReadOnlyList<T> lista) => lista[aleatorio.Next(lista.Count)];
}
