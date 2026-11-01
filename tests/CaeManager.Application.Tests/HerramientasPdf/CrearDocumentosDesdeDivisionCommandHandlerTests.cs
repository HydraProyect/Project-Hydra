using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

public class CrearDocumentosDesdeDivisionCommandHandlerTests
{
    private static readonly DateOnly Emision = new(2026, 1, 15);

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _empresaId = Guid.NewGuid();
    private readonly Guid _subcontrataId = Guid.NewGuid();
    private readonly Guid _trabajadorId = Guid.NewGuid();
    private readonly Guid _vehiculoId = Guid.NewGuid();
    private readonly Guid _tipoId = Guid.NewGuid();
    private readonly MediatorDeDivision _mediator = new();
    private readonly AlmacenEnMemoria _almacen = new();
    private readonly DivisorFalso _divisor = new();

    private CrearDocumentosDesdeDivisionCommandHandler Handler(Guid? tenant = null, bool disponible = true, AlcanceDatosServiceFalso? alcance = null)
    {
        _mediator.Catalogo = Result.Exito(new CatalogoDivisionPdf(
            new EmpresaDivisionPdf(_empresaId, "Montajes Norte S.L.", "B12345678"),
            [new EmpresaDivisionPdf(_subcontrataId, "Pintura Sur S.L.", null)],
            [new TrabajadorDivisionPdf(_trabajadorId, "Ana", "Pérez", null, _subcontrataId)],
            [new VehiculoDivisionPdf(_vehiculoId, "Furgoneta", "1234 KLM", _empresaId)],
            [new TipoDocumentoDivisionPdf(_tipoId, "Certificado", AmbitoAplicacion.Empresa, false, null, [])]));

        return new CrearDocumentosDesdeDivisionCommandHandler(
            new Disponibilidad(disponible), new TenantFijo(tenant ?? _tenantId), _mediator, _divisor, _almacen,
            alcance ?? new AlcanceDatosServiceFalso(),
            NullLogger<CrearDocumentosDesdeDivisionCommandHandler>.Instance);
    }

    private CrearDocumentosDesdeDivisionCommand Comando(params TramoACrear[] tramos) =>
        new([1, 2, 3], "paquete.pdf", _tenantId, _empresaId, tramos);

    private TramoACrear Tramo(int inicial, int final, ClaseTitular clase, Guid titular) =>
        new(inicial, final, clase, titular, _tipoId, Emision, null, false);

    [Fact]
    public async Task Una_Empresa_gestionada_fuera_de_la_cartera_no_crea_nada()
    {
        var resultado = await Handler(alcance: new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: []))
            .Handle(Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        _mediator.Altas.Should().BeEmpty();
        _almacen.Guardados.Should().BeEmpty();
    }
    [Fact]
    public async Task Cada_tramo_pasa_por_el_alta_normal_con_su_propietario_y_su_archivo()
    {
        var resultado = await Handler().Handle(Comando(
            Tramo(1, 2, ClaseTitular.EmpresaGestionada, _empresaId),
            Tramo(3, 3, ClaseTitular.Subcontrata, _subcontrataId),
            Tramo(4, 4, ClaseTitular.Trabajador, _trabajadorId),
            Tramo(5, 6, ClaseTitular.Vehiculo, _vehiculoId)), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().OnlyContain(r => r.Estado == EstadoTramoCreado.Creado && r.DocumentoId != null);

        var altas = _mediator.Altas;
        altas.Select(a => (a.EmpresaId, a.TrabajadorId, a.VehiculoId)).Should().Equal(
            (_empresaId, (Guid?)null, (Guid?)null),
            (_subcontrataId, null, null),
            (null, _trabajadorId, null),
            (null, null, _vehiculoId));
        altas.Should().OnlyContain(a => a.ClienteId == null && a.ProyectoId == null && a.TipoDocumentoId == _tipoId && a.FechaEmision == Emision);
        altas.Select(a => a.ArchivoUrl).Should().Equal(_almacen.Guardados.Keys);
        altas[0].Comentarios.Should().Be("Creado con Dividir: páginas 1-2 del PDF original.");
        _divisor.Extraidos.Should().Equal((1, 2), (3, 3), (4, 4), (5, 6));
    }

    [Fact]
    public async Task Si_el_selector_cambio_de_Tenant_se_rechaza_sin_leer_ni_crear_nada()
    {
        var resultado = await Handler(tenant: Guid.NewGuid()).Handle(
            Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado);
        _mediator.Altas.Should().BeEmpty();
        _almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_Tenant_resuelto_falla_cerrado()
    {
        var handler = new CrearDocumentosDesdeDivisionCommandHandler(
            new Disponibilidad(true), new TenantFijo(null), _mediator, _divisor, _almacen,
            new AlcanceDatosServiceFalso(),
            NullLogger<CrearDocumentosDesdeDivisionCommandHandler>.Instance);

        var resultado = await handler.Handle(Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado);
    }

    [Fact]
    public async Task Una_Empresa_gestionada_fuera_de_la_cartera_se_rechaza_con_el_error_del_catalogo()
    {
        var handler = Handler();
        _mediator.Catalogo = Result.Fallo<CatalogoDivisionPdf>(
            Error.Crear(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable, "fuera"));

        var resultado = await handler.Handle(Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        _almacen.Guardados.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ClaseTitular.EmpresaGestionada)]
    [InlineData(ClaseTitular.Subcontrata)]
    [InlineData(ClaseTitular.Trabajador)]
    [InlineData(ClaseTitular.Vehiculo)]
    public async Task Un_titular_ajeno_rechaza_la_peticion_entera_antes_de_crear_nada(ClaseTitular clase)
    {
        var resultado = await Handler().Handle(Comando(
            Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId),
            Tramo(2, 2, clase, Guid.NewGuid())), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTitularAjeno);
        _mediator.Altas.Should().BeEmpty();
        _almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Trabajador_declarado_como_Vehiculo_no_cuela_por_tener_un_id_conocido()
    {
        var resultado = await Handler().Handle(
            Comando(Tramo(1, 1, ClaseTitular.Vehiculo, _trabajadorId)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTitularAjeno);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    [InlineData(10, 11)]
    public async Task Paginas_fuera_del_PDF_no_se_crean(int inicial, int final)
    {
        var resultado = await Handler().Handle(
            Comando(Tramo(inicial, final, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTramosInvalidos);
    }

    [Fact]
    public async Task Dos_tramos_solapados_no_se_crean()
    {
        var resultado = await Handler().Handle(Comando(
            Tramo(1, 3, ClaseTitular.EmpresaGestionada, _empresaId),
            Tramo(3, 4, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTramosInvalidos);
        _mediator.Altas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tramo_de_mas_de_10_MB_avisa_y_no_se_crea_pero_los_demas_si()
    {
        _divisor.PaginaEnorme = 2;

        var resultado = await Handler().Handle(Comando(
            Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId),
            Tramo(2, 2, ClaseTitular.EmpresaGestionada, _empresaId),
            Tramo(3, 3, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Valor.Select(r => r.Estado).Should().Equal(
            EstadoTramoCreado.Creado, EstadoTramoCreado.DemasiadoGrande, EstadoTramoCreado.Creado);
        _mediator.Altas.Should().HaveCount(2);
        _almacen.Guardados.Should().HaveCount(2, "el tramo que no se crea tampoco se guarda");
    }

    [Fact]
    public async Task Exactamente_10_MB_si_se_crea()
    {
        _divisor.PaginaEnorme = 1;
        _divisor.TamanoEnorme = LimitesArchivoSubido.TamanoMaximoBytes;

        var resultado = await Handler().Handle(
            Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Valor.Single().Estado.Should().Be(EstadoTramoCreado.Creado);
    }

    [Fact]
    public async Task Un_tramo_que_el_alta_rechaza_retira_su_archivo_y_no_deshace_los_anteriores()
    {
        _mediator.RechazarAlta = n => n == 2 ? Error.Crear("Documento.Fallo", "Rechazado por el alta") : null;

        var resultado = await Handler().Handle(Comando(
            Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId),
            Tramo(2, 2, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Valor.Select(r => r.Estado).Should().Equal(EstadoTramoCreado.Creado, EstadoTramoCreado.Rechazado);
        resultado.Valor[1].Detalle.Should().Be("Rechazado por el alta");
        _almacen.Guardados.Should().HaveCount(1, "el archivo del tramo rechazado no se queda huérfano");
    }

    [Fact]
    public async Task Una_excepcion_del_alta_conserva_el_archivo_y_no_pierde_lo_ya_creado()
    {
        _mediator.LanzarEnAlta = n => n == 2 ? new InvalidOperationException("fallo tras guardar") : null;

        var resultado = await Handler().Handle(Comando(
            Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId),
            Tramo(2, 2, ClaseTitular.EmpresaGestionada, _empresaId),
            Tramo(3, 3, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Valor.Select(r => r.Estado).Should().Equal(
            EstadoTramoCreado.Creado, EstadoTramoCreado.SinConfirmar, EstadoTramoCreado.Creado);
        _almacen.Guardados.Should().HaveCount(3, "el Documento pudo guardarse antes del fallo y necesita su archivo");
    }

    [Fact]
    public async Task Una_validacion_del_alta_retira_el_archivo_y_rechaza_solo_ese_tramo()
    {
        _mediator.LanzarEnAlta = n => n == 1
            ? new ValidationException([new ValidationFailure("FechaEmision", "No válida")])
            : null;

        var resultado = await Handler().Handle(
            Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Valor.Single().Estado.Should().Be(EstadoTramoCreado.Rechazado);
        resultado.Valor.Single().Detalle.Should().Be("No válida");
        _almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_al_guardar_un_tramo_no_pierde_lo_ya_creado()
    {
        _almacen.FallarEn = "paquete-pag-2-2.pdf";

        var resultado = await Handler().Handle(Comando(
            Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId),
            Tramo(2, 2, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Valor.Select(r => r.Estado).Should().Equal(EstadoTramoCreado.Creado, EstadoTramoCreado.Rechazado);
    }

    [Fact]
    public async Task Una_fecha_de_emision_futura_en_cualquier_tramo_no_crea_ninguno()
    {
        var futura = new TramoACrear(2, 2, ClaseTitular.EmpresaGestionada, _empresaId, _tipoId,
            DiaDeNegocio.Hoy().AddDays(1), null, false);

        var resultado = await Handler().Handle(
            Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId), futura), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTramosInvalidos);
        resultado.Error.Mensaje.Should().Be("La fecha de emisión no puede ser futura.");
        _mediator.Altas.Should().BeEmpty();
        _almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tipo_fuera_del_catalogo_no_crea_ninguno()
    {
        var ajeno = new TramoACrear(2, 2, ClaseTitular.EmpresaGestionada, _empresaId, Guid.NewGuid(), Emision, null, false);

        var resultado = await Handler().Handle(
            Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId), ajeno), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTramosInvalidos);
        _mediator.Altas.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_herramienta_no_esta_disponible_no_hace_nada()
    {
        var resultado = await Handler(disponible: false).Handle(
            Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(PrepararDivisionPdfQueryHandler.ErrorNoDisponible);
        _mediator.Altas.Should().BeEmpty();
    }

    [Fact]
    public void El_validador_exige_tramos_y_acota_el_original()
    {
        var validador = new CrearDocumentosDesdeDivisionCommandValidator();

        validador.Validate(Comando()).IsValid.Should().BeFalse();
        validador.Validate(Comando(Tramo(1, 1, ClaseTitular.EmpresaGestionada, _empresaId))).IsValid.Should().BeTrue();
    }

    private sealed class Disponibilidad(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class DivisorFalso : IDivisorPdf
    {
        public List<(int, int)> Extraidos { get; } = [];
        public int? PaginaEnorme { get; set; }
        public long TamanoEnorme { get; set; } = LimitesArchivoSubido.TamanoMaximoBytes + 1;

        public Result<IPdfParaDividir> Abrir(byte[] contenidoPdf) => Result.Exito<IPdfParaDividir>(new Abierto(this));

        private sealed class Abierto(DivisorFalso divisor) : IPdfParaDividir
        {
            public int NumeroPaginas => 6;
            public bool EstaFirmado => false;

            public Result<byte[]> ExtraerPaginas(int paginaInicial, int paginaFinal)
            {
                divisor.Extraidos.Add((paginaInicial, paginaFinal));
                return Result.Exito(new byte[paginaInicial == divisor.PaginaEnorme ? divisor.TamanoEnorme : 8]);
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class AlmacenEnMemoria : IFileStorageService
    {
        public Dictionary<string, long> Guardados { get; } = [];
        public string? FallarEn { get; set; }

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            if (nombreArchivoOriginal == FallarEn)
                throw new IOException("disco lleno");

            Guardados[nombreArchivoOriginal] = contenido.Length;
            return Task.FromResult(nombreArchivoOriginal);
        }

        public Task<Stream> AbrirAsync(string archivoUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string archivoUrl, CancellationToken cancellationToken = default)
        {
            Guardados.Remove(archivoUrl);
            return Task.CompletedTask;
        }
    }

    private sealed class MediatorDeDivision : IMediator
    {
        public Result<CatalogoDivisionPdf>? Catalogo { get; set; }
        public List<CrearDocumentoCommand> Altas { get; } = [];
        public Func<int, Error?> RechazarAlta { get; set; } = _ => null;
        public Func<int, Exception?> LanzarEnAlta { get; set; } = _ => null;
        private int _intentos;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch // el tipo real lo fija el handler
            {
                ObtenerCatalogoDivisionPdfQuery => Catalogo!,
                CrearDocumentoCommand alta => Alta(alta),
                _ => throw new NotSupportedException(request.GetType().Name)
            };

            return Task.FromResult((TResponse)respuesta);
        }

        private Result<Guid> Alta(CrearDocumentoCommand alta)
        {
            if (LanzarEnAlta(++_intentos) is { } excepcion)
                throw excepcion;

            return RechazarAlta(Altas.Count + 1) is { } error
                ? Rechazar(alta, error)
                : Aceptar(alta);
        }

        private Result<Guid> Aceptar(CrearDocumentoCommand alta)
        {
            Altas.Add(alta);
            return Result.Exito(Guid.NewGuid());
        }

        private static Result<Guid> Rechazar(CrearDocumentoCommand _, Error error) => Result.Fallo<Guid>(error);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification =>
            throw new NotSupportedException();
    }
}
