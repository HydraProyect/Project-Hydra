using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Documentos;
using ClosedXML.Excel;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-20: la columna «Plataformas CAE» del Excel del listado de Documentos: «Nalanda: Pendiente de subir; Dokify: Validada», con los
/// rótulos de D-22 y la misma regla que la insignia de la fila. Nunca el motivo del rechazo, la dirección del portal ni la anotación
/// del Centro 360, y con el rol Usuario de Cliente el archivo no dice nada que la pantalla no enseñe ya. El endpoint no filtra por
/// rol (la consulta ya llega acotada): estos tests miran el contenido de cada celda del archivo.
/// </summary>
public class DocumentosExportacionPlataformasTests
{
    private const string Motivo = "Firma ilegible en la página 3";
    private const string Nota = "Anotado a mano por el Gestor CAE tras llamar a la plataforma";
    private const string Usuario = "Marta Gestora";

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private sealed class RegistroFalso : IRegistroExportacionService
    {
        public Task RegistrarAsync(string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MediadorFalso(IReadOnlyList<DocumentoListaDto> documentos) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerClientesAutorizadosQuery)
                return Task.FromResult((TResponse)(object)Array.Empty<ClienteAutorizadoDto>());

            var consulta = (ObtenerDocumentosQuery)(object)request;
            return Task.FromResult((TResponse)(object)new ResultadoPaginado<DocumentoListaDto>(
                documentos, documentos.Count, consulta.Pagina, consulta.TamanoPagina));
        }
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }

    private static DocumentoListaDto Documento(string propietario, params AcreditacionResumenDto[] acreditaciones) => new(
        Guid.NewGuid(), AmbitoAplicacion.Trabajador, propietario, "Reconocimiento médico",
        new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15), EstadoDocumento.Vigente, "blob/secreto.pdf", acreditaciones);

    private static AcreditacionResumenDto Acreditacion(string plataforma, EstadoAcreditacion estado, bool conAnotacion = false) =>
        new(Guid.NewGuid(), plataforma, estado,
            conAnotacion ? new AnotacionEstadoPlataformaDto(Guid.NewGuid(), new DateOnly(2026, 11, 1), Motivo, Nota, Usuario) : null);

    private static async Task<(IXLWorksheet Hoja, XLWorkbook Libro)> ExportarAsync(string rol, params DocumentoListaDto[] documentos)
    {
        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba"))
        };
        var resultado = await DocumentosEndpoints.ExportarAsync(contexto, new MediadorFalso(documentos),
            new TenantActualFalso(Guid.NewGuid()), new RegistroFalso(), default);

        var flujo = new MemoryStream();
        await resultado.Should().BeOfType<FileStreamHttpResult>().Subject.FileStream.CopyToAsync(flujo);
        flujo.Position = 0;
        var libro = new XLWorkbook(flujo);
        return (libro.Worksheet("Documentos"), libro);
    }

    [Fact]
    public async Task La_hoja_gana_la_columna_Plataformas_CAE_al_final_sin_mover_las_de_siempre()
    {
        var (hoja, libro) = await ExportarAsync(Roles.GestorCae, Documento("Lucía Ferrer", Acreditacion("Nalanda", EstadoAcreditacion.PendienteDeSubir)));
        using var _ = libro;

        Enumerable.Range(1, 7).Select(c => hoja.Cell(1, c).GetString()).Should().Equal(
            "Propietario", "Ámbito", "Tipo de documento", "Emisión", "Vencimiento", "Estado", "Plataformas CAE");
        hoja.Cell(2, 1).GetString().Should().Be("Lucía Ferrer");
        hoja.Cell(2, 6).GetString().Should().Be("Vigente");
    }

    [Fact]
    public async Task La_celda_dice_Plataforma_dos_puntos_estado_separadas_por_punto_y_coma_con_los_rotulos_de_D22()
    {
        var (hoja, libro) = await ExportarAsync(Roles.GestorCae, Documento("Lucía Ferrer",
            Acreditacion("Dokify", EstadoAcreditacion.Aceptada),
            Acreditacion("Nalanda", EstadoAcreditacion.PendienteDeSubir)));
        using var _ = libro;

        // Primero la más grave (pendiente de subir), luego la validada.
        hoja.Cell(2, 7).GetString().Should().Be("Nalanda: Pendiente de subir; Dokify: Validada");
    }

    [Fact]
    public async Task Una_plataforma_con_varios_Centros_sale_una_vez_con_su_estado_mas_grave()
    {
        var (hoja, libro) = await ExportarAsync(Roles.GestorCae, Documento("Lucía Ferrer",
            Acreditacion("Nalanda", EstadoAcreditacion.Aceptada),
            Acreditacion("Nalanda", EstadoAcreditacion.Rechazada),
            Acreditacion("nalanda", EstadoAcreditacion.Subida)));
        using var _ = libro;

        hoja.Cell(2, 7).GetString().Should().Be("Nalanda: Rechazada");
    }

    [Fact]
    public async Task Un_documento_sin_acreditaciones_deja_la_celda_vacia()
    {
        var (hoja, libro) = await ExportarAsync(Roles.GestorCae, Documento("Lucía Ferrer"));
        using var _ = libro;

        hoja.Cell(2, 7).GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_nombre_de_plataforma_que_empieza_por_igual_se_exporta_como_texto_y_no_como_formula()
    {
        var (hoja, libro) = await ExportarAsync(Roles.GestorCae, Documento("Lucía Ferrer", Acreditacion("=1+1", EstadoAcreditacion.Aceptada)));
        using var _ = libro;

        hoja.Cell(2, 7).DataType.Should().Be(XLDataType.Text);
        hoja.Cell(2, 7).HasFormula.Should().BeFalse();
        hoja.Cell(2, 7).GetString().Should().Be("=1+1: Validada");
    }

    [Theory]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public async Task El_archivo_nunca_lleva_el_motivo_del_rechazo_ni_la_anotacion_ni_el_archivo_del_documento(string rol)
    {
        var (hoja, libro) = await ExportarAsync(rol, Documento("Lucía Ferrer",
            Acreditacion("Nalanda", EstadoAcreditacion.Rechazada, conAnotacion: true),
            Acreditacion("Dokify", EstadoAcreditacion.Aceptada, conAnotacion: true)));
        using var _ = libro;

        var texto = string.Join('\n', hoja.CellsUsed().Select(c => c.GetString()));
        texto.Should().NotContain(Motivo).And.NotContain(Nota).And.NotContain(Usuario).And.NotContain("secreto.pdf");
        hoja.Cell(2, 7).GetString().Should().Be("Nalanda: Rechazada; Dokify: Validada");
    }

    /// <summary>
    /// Con el rol Usuario de Cliente: el archivo no expone nada que la pantalla no enseñe ya. La celda es, palabra por palabra, el
    /// texto de las insignias de plataformas de la fila (PlataformasCaeDelDocumento es la misma regla en los dos sitios), y el archivo
    /// solo tiene las siete columnas del listado.
    /// </summary>
    [Fact]
    public async Task Con_el_rol_Usuario_de_Cliente_el_archivo_solo_dice_lo_que_la_pantalla_ya_enseña()
    {
        var acreditaciones = new[]
        {
            Acreditacion("Nalanda", EstadoAcreditacion.Rechazada, conAnotacion: true),
            Acreditacion("Dokify", EstadoAcreditacion.Aceptada, conAnotacion: true),
            Acreditacion("Ctaima", EstadoAcreditacion.PendienteDeSubir),
        };
        var (hoja, libro) = await ExportarAsync(Roles.Cliente, Documento("Lucía Ferrer", acreditaciones));
        using var _ = libro;

        var enPantalla = PlataformasCaeDelDocumento.Resumir(acreditaciones)
            .Select(a => $"{a.NombrePlataforma}: {EstadoAcreditacionUi.TextoCapitalizado(a.Estado)}");
        hoja.Cell(2, 7).GetString().Should().Be(string.Join("; ", enPantalla));
        hoja.LastColumnUsed()!.ColumnNumber().Should().Be(7);
        hoja.RowsUsed().Should().HaveCount(2, "la cabecera y una fila: nada más que el listado");
    }
}
