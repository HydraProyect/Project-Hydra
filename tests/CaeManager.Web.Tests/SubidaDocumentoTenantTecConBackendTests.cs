using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.AutorizacionesLectura.Commands.SubirDocumentoRequerimientoTec;
using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Inbound.TenantTec.Pages;
using CaeManager.Web.Features.Inbound.TenantTec.Servicios;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El adaptador de la subida del Tenant TEC (demo Inbound, P2): busca el trío del requerimiento en la lectura, guarda el
/// archivo como un PDF y manda <see cref="SubirDocumentoRequerimientoTecCommand"/>; la pantalla, con los dos adaptadores
/// reales, sube y vuelve a leer el estado. Lo que el comando revalida lo prueban sus tests de Application. Datos ficticios.
/// </summary>
public sealed class SubidaDocumentoTenantTecConBackendTests : BunitContext
{
    private static readonly Guid ContratoId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    public SubidaDocumentoTenantTecConBackendTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
    }

    private static RequerimientoTecDto Fila(string tipo, string sujetoNombre, bool conDocumento = false) =>
        new(Guid.NewGuid(), ContratoId, Guid.NewGuid(), SujetoRequerimientoTec.Trabajador, Guid.NewGuid(), "Logística Delta Sur",
            "Logística Delta Sur S.A.", "Planta Zaragoza Norte", tipo, "Certificado vigente", "DeCentro", true, "Montajes Moncayo S.L.",
            sujetoNombre, conDocumento ? EstadoRequerimientoTecSinVeredicto.PendienteDeValidar : EstadoRequerimientoTecSinVeredicto.PendienteDeSubir,
            conDocumento ? EstadoDocumento.Vigente : null, null, conDocumento ? 1 : 0, false);

    /// <summary>La lectura y el comando: al recibir una subida con éxito, la fila del trío pasa a «Pendiente de validar».</summary>
    private sealed class MediadorDePrueba(List<RequerimientoTecDto> filas, Result<Guid>? respuesta = null) : IMediator
    {
        public List<SubirDocumentoRequerimientoTecCommand> Subidas { get; } = [];

        public int Consultas { get; private set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            switch (request)
            {
                case ObtenerRequerimientosTecQuery:
                    Consultas++;
                    return (TResponse)(object)filas.ToList();
                case SubirDocumentoRequerimientoTecCommand subida:
                    Subidas.Add(subida);
                    var resultado = respuesta ?? Result.Exito(Guid.NewGuid());
                    if (resultado.EsExitoso)
                    {
                        var i = filas.FindIndex(f => f.ContratoAccesoId == subida.ContratoAccesoId && f.RequisitoId == subida.RequisitoId
                                                     && f.Sujeto == subida.Sujeto && f.SujetoId == subida.SujetoId);
                        filas[i] = filas[i] with
                        {
                            Estado = EstadoRequerimientoTecSinVeredicto.PendienteDeValidar,
                            EstadoDocumento = EstadoDocumento.Vigente,
                            DocumentosVigentes = 1,
                        };
                    }

                    return (TResponse)(object)resultado;
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AlmacenEnMemoria : IFileStorageService
    {
        public Dictionary<string, byte[]> Guardados { get; } = [];

        public List<string> Eliminados { get; } = [];

        public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            using var memoria = new MemoryStream();
            await contenido.CopyToAsync(memoria, cancellationToken);
            var clave = $"clave-{Guardados.Count + 1}-{nombreArchivoOriginal}";
            Guardados[clave] = memoria.ToArray();
            return clave;
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Guardados[identificador]));

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
        {
            Eliminados.Add(identificador);
            return Task.CompletedTask;
        }
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Un PDF no pasa por el conversor de Word.");
    }

    private static IPresupuestoMemoriaDocumentos Presupuesto() =>
        new PresupuestoMemoriaDocumentos(Options.Create(new OpcionesPresupuestoMemoriaDocumentos()), TimeProvider.System);

    private static byte[] PdfDeUnaPagina()
    {
        using var documento = new PdfSharp.Pdf.PdfDocument();
        documento.AddPage();
        using var memoria = new MemoryStream();
        documento.Save(memoria);
        return memoria.ToArray();
    }

    private static SubidaDocumentoTenantTecConBackend Adaptador(MediadorDePrueba mediador, AlmacenEnMemoria almacen) =>
        new(mediador, almacen, new ConversorQueNadieDebeTocar(), Presupuesto(), NullLogger<SubidaDocumentoTenantTecConBackend>.Instance);

    private static SolicitudSubidaTec Solicitud(Guid requerimientoId, byte[] contenido, string nombre = "formacion.pdf", DateOnly? emision = null) =>
        new(requerimientoId, nombre, "application/pdf", contenido.Length, new MemoryStream(contenido), emision, null, "Curso de 2026");

    [Fact]
    public async Task Un_PDF_se_guarda_y_se_manda_con_el_trio_del_requerimiento_y_ofrece_el_siguiente_pendiente()
    {
        var fila = Fila("Formación en altura", "Luis Gómez Ara");
        var otra = Fila("Alta en la Seguridad Social", "Marta Ruiz");
        var mediador = new MediadorDePrueba([fila, otra]);
        var almacen = new AlmacenEnMemoria();
        var emision = DiaDeNegocio.Hoy().AddDays(-3);

        var respuesta = await Adaptador(mediador, almacen).SubirAsync(Solicitud(fila.Id, PdfDeUnaPagina(), emision: emision));

        respuesta.Resultado.Should().Be(ResultadoSubidaTec.Subido);
        respuesta.SiguientePendienteId.Should().Be(otra.Id);
        respuesta.SiguientePendiente.Should().Be("Alta en la Seguridad Social · Marta Ruiz");
        var subida = mediador.Subidas.Should().ContainSingle().Subject;
        subida.ContratoAccesoId.Should().Be(fila.ContratoAccesoId);
        subida.RequisitoId.Should().Be(fila.RequisitoId);
        subida.Sujeto.Should().Be(SujetoRequerimientoTec.Trabajador);
        subida.SujetoId.Should().Be(fila.SujetoId);
        subida.FechaEmision.Should().Be(emision);
        subida.Comentarios.Should().Be("Curso de 2026");
        almacen.Guardados.Should().ContainKey(subida.ArchivoUrl);
        almacen.Guardados[subida.ArchivoUrl].Take(4).Should().Equal("%PDF"u8.ToArray());
        almacen.Eliminados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_emision_se_toma_hoy_como_en_el_alta_individual()
    {
        var fila = Fila("Formación en altura", "Luis Gómez Ara");
        var mediador = new MediadorDePrueba([fila]);

        await Adaptador(mediador, new AlmacenEnMemoria()).SubirAsync(Solicitud(fila.Id, PdfDeUnaPagina()));

        mediador.Subidas.Should().ContainSingle().Which.FechaEmision.Should().Be(DiaDeNegocio.Hoy());
    }

    [Fact]
    public async Task Si_el_comando_falla_borra_el_archivo_que_ningun_documento_adopta_y_responde_error()
    {
        var fila = Fila("Formación en altura", "Luis Gómez Ara");
        var mediador = new MediadorDePrueba([fila], Result.Fallo<Guid>(SubirDocumentoRequerimientoTecErrores.SujetoNoAsignado));
        var almacen = new AlmacenEnMemoria();

        var respuesta = await Adaptador(mediador, almacen).SubirAsync(Solicitud(fila.Id, PdfDeUnaPagina()));

        respuesta.Resultado.Should().Be(ResultadoSubidaTec.Error);
        almacen.Eliminados.Should().Equal(almacen.Guardados.Keys);
    }

    [Fact]
    public async Task Un_archivo_cuyo_contenido_no_es_un_documento_no_se_guarda_ni_se_manda()
    {
        var fila = Fila("Formación en altura", "Luis Gómez Ara");
        var mediador = new MediadorDePrueba([fila]);
        var almacen = new AlmacenEnMemoria();

        var respuesta = await Adaptador(mediador, almacen).SubirAsync(Solicitud(fila.Id, "esto no es un PDF"u8.ToArray()));

        respuesta.Resultado.Should().Be(ResultadoSubidaTec.Error);
        mediador.Subidas.Should().BeEmpty();
        almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_requerimiento_que_ya_no_esta_en_la_lectura_no_guarda_nada()
    {
        var mediador = new MediadorDePrueba([Fila("Formación en altura", "Luis Gómez Ara")]);
        var almacen = new AlmacenEnMemoria();

        var respuesta = await Adaptador(mediador, almacen).SubirAsync(Solicitud(Guid.NewGuid(), PdfDeUnaPagina()));

        respuesta.Resultado.Should().Be(ResultadoSubidaTec.Error);
        mediador.Subidas.Should().BeEmpty();
        almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public void La_pantalla_con_los_adaptadores_reales_sube_y_al_cerrar_refresca_el_estado_del_requerimiento()
    {
        var fila = Fila("Formación en altura", "Luis Gómez Ara");
        var mediador = new MediadorDePrueba([fila]);
        var almacen = new AlmacenEnMemoria();
        Services.AddSingleton<IMediator>(mediador);
        Services.AddSingleton<IFileStorageService>(almacen);
        Services.AddSingleton<IConversorWordPdfService>(new ConversorQueNadieDebeTocar());
        Services.AddSingleton(Presupuesto());
        Services.AddScoped<IRequerimientosTenantTec, RequerimientosTenantTecConBackend>();
        Services.AddScoped<ISubidaDocumentoTenantTec, SubidaDocumentoTenantTecConBackend>();

        var cut = Render<Requerimientos>(p => p.Add(x => x.RequerimientoId, fila.Id));
        cut.WaitForAssertion(() => cut.Find("[role=dialog]").TextContent.Should().Contain("Formación en altura"));
        FilaDe(cut, "Formación en altura").TextContent.Should().Contain("Pendiente de subir");

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(PdfDeUnaPagina(), "formacion.pdf", contentType: "application/pdf"));
        BotonDelDialogo(cut, "Subir").Click();

        cut.WaitForAssertion(() => cut.Find("[role=dialog]").TextContent.Should().Contain("Pendiente de validar"));
        mediador.Subidas.Should().ContainSingle().Which.SujetoId.Should().Be(fila.SujetoId);
        var consultasAntesDeCerrar = mediador.Consultas;

        BotonDelDialogo(cut, "Más tarde").Click();

        cut.WaitForAssertion(() => mediador.Consultas.Should().BeGreaterThan(consultasAntesDeCerrar));
        cut.WaitForAssertion(() =>
        {
            var refrescada = FilaDe(cut, "Formación en altura");
            refrescada.TextContent.Should().Contain("Pendiente de validar");
            refrescada.QuerySelectorAll("button").Should().NotContain(b => b.TextContent.Trim() == "Subir");
        });
    }

    private static IElement FilaDe(IRenderedComponent<Requerimientos> cut, string nombre) =>
        cut.FindAll("tbody tr").Single(f => f.TextContent.Contains(nombre));

    private static IElement BotonDelDialogo(IRenderedComponent<Requerimientos> cut, string texto) =>
        cut.Find("[role=dialog]").QuerySelectorAll("button").First(b => b.TextContent.Trim() == texto);
}
