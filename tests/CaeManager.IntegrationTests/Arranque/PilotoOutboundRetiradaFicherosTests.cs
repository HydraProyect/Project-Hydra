using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.CrearPlantillaDocumento;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Plantillas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using FluentAssertions.Execution;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// La retirada del lote del piloto Outbound frente a las dos familias de ficheros del almacén
/// que ni la siembra ni un ensayo corriente escriben: el adjunto de un mensaje
/// (<c>AdjuntosMensaje.ArchivoUrl</c>) y el original de una plantilla de documento
/// (<c>PlantillasDocumentoVersion.ArchivoOriginalUrl</c>). Sin estos dos ficheros plantados,
/// quitar sus dos consultas de <see cref="PilotoOutboundRetirada.ClavesDeFicherosAsync"/> dejaba
/// toda la suite en verde (medido por mutación el 2026-10-09).
///
/// <para>
/// Las otras tres familias —el logo del Tenant, los PDF de los documentos (también los
/// descartados) y las plantillas en blanco de los requisitos de Centro de Trabajo— y el fichero
/// de un Tenant ajeno al lote se miden en <see cref="PilotoOutboundAdministrativaTests"/>.
/// </para>
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundRetiradaFicherosTests(ITestOutputHelper salida)
{
    // Las cabeceras de un ZIP y de un PDF: el almacén cifra lo que recibe y no mira el contenido.
    private static readonly byte[] ContenidoAdjunto = [80, 75, 3, 4, 20, 0, 0, 0];
    private static readonly byte[] ContenidoPlantilla = [37, 80, 68, 70, 45, 49, 46, 55];

    private static string RutaEnElAlmacen(ArnesPilotoOutbound arnes, string clave) =>
        Path.Combine(arnes.DirectorioAlmacen, clave.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Las filas del Tenant propietario en las tablas que este test puebla, contadas con la identidad
    /// de bootstrap y sin filtros: el mismo instrumento antes de retirar (control positivo) y después.
    /// </summary>
    private static Task<Dictionary<string, int>> FilasPlantadasAsync(ArnesPilotoOutbound arnes, Guid tenantId) =>
        arnes.ComoBootstrapAsync(async b => new Dictionary<string, int>
        {
            ["Conversaciones"] = await b.Conversaciones.IgnoreQueryFilters().CountAsync(c => c.TenantId == tenantId),
            ["Mensajes"] = await b.Mensajes.IgnoreQueryFilters().CountAsync(m => m.TenantId == tenantId),
            ["Adjuntos de mensaje"] = await b.AdjuntosMensaje.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId),
            ["Plantillas de documento"] = await b.PlantillasDocumento.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenantId),
            ["Versiones de plantilla de documento"] = await b.PlantillasDocumentoVersion.IgnoreQueryFilters().CountAsync(v => v.TenantId == tenantId),
        });

    /// <summary>
    /// El adjunto de un mensaje, con entidades y el contexto de tráfico dentro del ámbito del Tenant
    /// propietario. No va por el comando del producto: los dos que adjuntan (responder en un hilo y
    /// redactar un mensaje nuevo) exigen un buzón de Microsoft 365 conectado o el remitente simulado, que
    /// este arnés no activa. El fichero lo guarda el almacén real, y la fila lleva la clave que él devuelve:
    /// lo mismo que hace el comando después de enviar.
    /// </summary>
    private static Task<string> PlantarAdjuntoDeMensajeAsync(ArnesPilotoOutbound arnes, Guid tenantId) =>
        arnes.EnTenantAsync(tenantId, async (db, sp) =>
        {
            using var contenido = new MemoryStream(ContenidoAdjunto);
            var clave = await sp.GetRequiredService<IFileStorageService>().GuardarAsync(contenido, "paquete-documental.zip");

            var conversacion = new Conversacion("Ensayo de la retirada: un hilo con un adjunto");
            conversacion
                .AgregarMensaje(
                    DireccionMensaje.Saliente, CanalConversacion.Correo, $"gestion-cae@{ContactosPilotoOutbound.DominioPorDefecto}",
                    "<p>Adjuntamos el paquete documental de la Visita.</p>")
                .AgregarAdjunto("paquete-documental.zip", "application/zip", ContenidoAdjunto.LongLength, clave);
            db.Conversaciones.Add(conversacion);
            await db.SaveChangesAsync();

            return clave;
        });

    /// <summary>
    /// El original de una plantilla de documento, por el comando real del producto y con su
    /// autorización: lo envía el Administrador del Tenant propietario T1, la cuenta que la siembra crea.
    /// </summary>
    private static Task<string?> PlantarOriginalDePlantillaDeDocumentoAsync(ArnesPilotoOutbound arnes, Guid tenantId) =>
        arnes.ComoCuentaAsync(CatalogoPilotoOutbound.NombreTenantT1, CuentasPilotoOutbound.Locales.AdministradorT1, async sp =>
        {
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var db = sp.GetRequiredService<CaeManagerDbContext>();
                var tipo = await db.TiposDocumento.OrderBy(t => t.Id).Select(t => new { t.Id, t.AmbitoAplicacion }).FirstAsync();

                var alta = await sp.GetRequiredService<ISender>().Send(new CrearPlantillaDocumentoCommand(
                    "Ensayo de la retirada: plantilla externa", tipo.AmbitoAplicacion, FormatoOrigenPlantilla.PdfVisual,
                    tipo.Id, ContenidoPlantilla, "plantilla-original.pdf"));
                alta.EsExitoso.Should().BeTrue(
                    "control: el alta de una plantilla de documento por el comando del producto tiene que salir bien ({0})",
                    alta.EsFallido ? $"{alta.Error.Codigo}: {alta.Error.Mensaje}" : "sin error");

                var versionId = alta.Valor.PlantillaDocumentoVersionId;
                return await db.PlantillasDocumentoVersion.Where(v => v.Id == versionId).Select(v => v.ArchivoOriginalUrl).SingleAsync();
            }
        });

    [Fact]
    public async Task La_retirada_borra_del_almacen_el_adjunto_de_un_mensaje_y_el_original_de_una_plantilla_de_documento_del_Tenant_propietario_y_el_rojo_nombra_la_familia()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var sinPiloto = await arnes.RecuentoAsync();

        (await arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion())))
            .Should().NotBeNull("control: la siembra del piloto está activa y escribe el lote");
        var idT1 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT1);
        var ficherosDeLaSiembra = arnes.FicherosEnElAlmacen();

        // ── A. Lo que un ensayo puede dejar en T1 y la siembra no escribe ──
        var claveAdjunto = await PlantarAdjuntoDeMensajeAsync(arnes, idT1);
        var clavePlantilla = await PlantarOriginalDePlantillaDeDocumentoAsync(arnes, idT1);
        clavePlantilla.Should().NotBeNullOrWhiteSpace("control: la versión de la plantilla de documento guarda la clave de su original");

        var familias = new (string Familia, string Clave)[]
        {
            ("adjunto de mensaje", claveAdjunto),
            ("original de plantilla de documento", clavePlantilla!),
        };

        // ── B. Controles positivos, antes de retirar ────────────────────
        // Sin ellos, «no existe» después de retirar no probaría nada.
        foreach (var (familia, clave) in familias)
            File.Exists(RutaEnElAlmacen(arnes, clave)).Should().BeTrue(
                "control: el fichero de la familia «{0}» ({1}) existe en el almacén antes de retirar", familia, clave);
        arnes.FicherosEnElAlmacen().Should().Be(
            ficherosDeLaSiembra + familias.Length, "control: los dos ficheros plantados son nuevos y no pisan ninguno de la siembra");

        // La identidad no privilegiada, dentro del ámbito de T1, ve cada clave en su fila: es lo que lee la retirada.
        var (filasConElAdjunto, filasConLaPlantilla) = await arnes.EnTenantAsync(idT1, async (db, _) => (
            await db.AdjuntosMensaje.CountAsync(a => a.ArchivoUrl == claveAdjunto),
            await db.PlantillasDocumentoVersion.CountAsync(v => v.ArchivoOriginalUrl == clavePlantilla)));
        filasConElAdjunto.Should().Be(1, "control: una fila de AdjuntosMensaje de T1 nombra la clave del adjunto de mensaje");
        filasConLaPlantilla.Should().Be(1, "control: una fila de PlantillasDocumentoVersion de T1 nombra la clave del original de plantilla de documento");

        var filasAntes = await FilasPlantadasAsync(arnes, idT1);
        salida.WriteLine("MEDIDO antes de retirar, filas de T1: " + string.Join(", ", filasAntes.Select(f => $"{f.Key} {f.Value}")));
        filasAntes.Should().OnlyContain(f => f.Value > 0, "control: el instrumento que después tiene que contar cero ve las filas plantadas");

        // ── C. Retirada ─────────────────────────────────────────────────
        (await arnes.RetirarAsync()).Select(r => r.NombreTenant).Should().Equal(
            CatalogoPilotoOutbound.NombresTenants, "MEDIDO: la retirada no se niega por las filas nuevas y retira el lote entero");

        // C.1 Familia a familia, por su clave y ANTES del recuento global: si la retirada deja de borrar una
        // familia, el rojo la nombra. El recuento solo diría cuántos ficheros sobran.
        using (new AssertionScope())
        {
            foreach (var (familia, clave) in familias)
                File.Exists(RutaEnElAlmacen(arnes, clave)).Should().BeFalse(
                    "MEDIDO: el fichero de la familia «{0}» ({1}) no sobrevive a la retirada de su Tenant propietario", familia, clave);
        }

        // C.2 Y sus filas tampoco.
        (await FilasPlantadasAsync(arnes, idT1)).Should().OnlyContain(
            f => f.Value == 0, "MEDIDO: la retirada borra los ficheros y después las filas que los nombraban");

        // C.3 El recuento global, al final: ni Tenants, ni cuentas, ni filas, ni ficheros del piloto.
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(
            sinPiloto, "MEDIDO: tras la retirada el almacén y la base quedan como antes de sembrar");
    }
}
