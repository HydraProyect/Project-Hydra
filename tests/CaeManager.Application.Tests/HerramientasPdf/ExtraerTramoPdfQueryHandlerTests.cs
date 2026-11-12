using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>
/// La salida «Descargar» de Dividir: extrae un tramo del PDF subido sin crear nada, y solo con Herramientas PDF
/// disponible, el Tenant del inicio de la pantalla y una Empresa propia de ese Tenant dentro del alcance de gestión,
/// igual que guardar la división. Ninguna negativa previa llega a abrir el PDF, y las mismas comprobaciones repetidas
/// tras la extracción impiden entregar el tramo si el acceso cambió mientras tanto.
/// </summary>
public class ExtraerTramoPdfQueryHandlerTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otroTenantId = Guid.NewGuid();
    private readonly Empresa _propia = new("Montajes Norte S.L.");
    private readonly Empresa _deOtroTenant = new("Ajena del otro Tenant S.L.");
    private readonly Empresa _subcontrata = Empresa.CrearComoSubcontrata("Pintura Sur S.L.", null, "Gestionada");
    private readonly DivisorFalso _divisor = new();
    private readonly EmpresasDelTenant _empresas;

    public ExtraerTramoPdfQueryHandlerTests()
    {
        AsignarTenant(_propia, _tenantId);
        AsignarTenant(_subcontrata, _tenantId);
        AsignarTenant(_deOtroTenant, _otroTenantId);
        _empresas = new EmpresasDelTenant(_tenantId);
        _empresas.ListaEmpresas.AddRange([_propia, _deOtroTenant, _subcontrata]);
    }

    private ExtraerTramoPdfQueryHandler Handler(
        bool disponible = true, AlcanceDatosServiceFalso? alcance = null, Guid? tenantActivo = null, TenantFijo? tenant = null) =>
        new(new Disponibilidad(disponible), tenant ?? new TenantFijo(tenantActivo ?? _tenantId), _empresas,
            alcance ?? new AlcanceDatosServiceFalso(), _divisor, NullLogger<ExtraerTramoPdfQueryHandler>.Instance);

    private ExtraerTramoPdfQuery Consulta(int inicial, int final, Guid? empresaId = null) =>
        new([1, 2, 3], "paquete de Ana.pdf", _tenantId, empresaId ?? _propia.Id, inicial, final);

    [Fact]
    public async Task Extrae_el_tramo_pedido_con_el_nombre_del_original_y_sus_paginas()
    {
        var resultado = await Handler().Handle(Consulta(2, 4), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.NombreArchivo.Should().Be("paquete de Ana-pag-2-4.pdf");
        resultado.Valor.Contenido.Should().Equal(2, 4);
        _divisor.Extraidos.Should().Equal((2, 4));
    }

    [Fact]
    public async Task Con_la_Empresa_en_la_cartera_del_Gestor_CAE_se_extrae()
    {
        var resultado = await Handler(alcance: new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: [_propia.Id]))
            .Handle(Consulta(1, 1), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        _divisor.Extraidos.Should().Equal((1, 1));
    }

    [Fact]
    public async Task Una_Empresa_fuera_de_la_cartera_no_abre_ni_extrae_nada()
    {
        var resultado = await Handler(alcance: new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: [Guid.NewGuid()]))
            .Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        _divisor.Abiertos.Should().Be(0);
    }

    [Fact]
    public async Task Con_el_alcance_sin_restriccion_una_Empresa_de_otro_Tenant_no_abre_el_PDF()
    {
        // El alcance sin restricción da por visible cualquier identificador; lo que decide es la lectura de la Empresa,
        // que el filtro de Tenant limita al Tenant activo.
        var resultado = await Handler().Handle(Consulta(1, 2, _deOtroTenant.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        _divisor.Abiertos.Should().Be(0);
    }

    [Fact]
    public async Task Con_el_alcance_sin_restriccion_una_Empresa_inexistente_no_abre_el_PDF()
    {
        var resultado = await Handler().Handle(Consulta(1, 2, Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        _divisor.Abiertos.Should().Be(0);
    }

    [Fact]
    public async Task Una_Subcontrata_no_vale_como_Empresa_gestionada_aunque_el_alcance_no_restrinja()
    {
        var resultado = await Handler().Handle(Consulta(1, 2, _subcontrata.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        _divisor.Abiertos.Should().Be(0);
    }

    [Fact]
    public async Task Si_el_selector_cambio_de_Tenant_se_rechaza_sin_abrir_el_PDF()
    {
        var resultado = await Handler(tenantActivo: _otroTenantId).Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado);
        _divisor.Abiertos.Should().Be(0);
    }

    [Fact]
    public async Task Sin_Tenant_resuelto_falla_cerrado_sin_abrir_el_PDF()
    {
        var handler = new ExtraerTramoPdfQueryHandler(new Disponibilidad(true), new TenantFijo(null), _empresas,
            new AlcanceDatosServiceFalso(), _divisor, NullLogger<ExtraerTramoPdfQueryHandler>.Instance);

        var resultado = await handler.Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado);
        _divisor.Abiertos.Should().Be(0);
    }

    [Fact]
    public async Task Sin_Herramientas_PDF_disponible_no_abre_nada()
    {
        var resultado = await Handler(disponible: false).Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(PrepararDivisionPdfQueryHandler.ErrorNoDisponible);
        _divisor.Abiertos.Should().Be(0);
    }

    [Fact]
    public async Task Un_tramo_que_se_sale_del_PDF_se_rechaza_sin_extraer()
    {
        var resultado = await Handler().Handle(Consulta(5, 7), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresDivisorPdf.RangoInvalido);
        _divisor.Extraidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_PDF_que_no_se_puede_abrir_devuelve_el_fallo_del_divisor()
    {
        _divisor.FalloAlAbrir = Error.Crear(ErroresDivisorPdf.ConContrasena, "Con contraseña.");

        var resultado = await Handler().Handle(Consulta(1, 1), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresDivisorPdf.ConContrasena);
    }

    [Fact]
    public async Task Si_el_divisor_lanza_es_un_fallo_interno_de_ese_tramo_registrado_como_error_y_no_un_PDF_ilegible()
    {
        // El divisor tipa lo que depende del PDF; lo que lanza (no arrancar el proceso aparte, una tubería rota) es de la
        // instancia: no se dice que el PDF esté dañado.
        _divisor.ExcepcionAlExtraer = new System.ComponentModel.Win32Exception("no se pudo arrancar el proceso aparte");
        var registro = new RegistroDoble();
        var handler = new ExtraerTramoPdfQueryHandler(new Disponibilidad(true), new TenantFijo(_tenantId), _empresas,
            new AlcanceDatosServiceFalso(), _divisor, registro);

        var resultado = await handler.Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresDivisorPdf.FalloInterno);
        _divisor.Cerrados.Should().Be(1, "el PDF abierto se cierra aunque la extracción lance");
        registro.Entradas.Should().ContainSingle().Which.Should().Match<(Microsoft.Extensions.Logging.LogLevel Nivel, string Mensaje)>(
            e => e.Nivel == Microsoft.Extensions.Logging.LogLevel.Error
                 && e.Mensaje.Contains("Win32Exception") && !e.Mensaje.Contains("no se pudo arrancar"),
            "se registra como error con el tipo, sin el mensaje de la excepción");
    }

    /// <summary>Anota cada entrada del registro con su nivel y su mensaje ya formateado.</summary>
    private sealed class RegistroDoble : Microsoft.Extensions.Logging.ILogger<ExtraerTramoPdfQueryHandler>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task La_cancelacion_no_se_disfraza_de_fallo_del_tramo()
    {
        _divisor.ExcepcionAlExtraer = new OperationCanceledException();

        var accion = () => Handler().Handle(Consulta(1, 2), CancellationToken.None);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    public void El_validador_rechaza_rangos_sin_sentido(int inicial, int final)
    {
        new ExtraerTramoPdfQueryValidator().Validate(Consulta(inicial, final)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void El_validador_acepta_un_rango_de_una_pagina()
    {
        new ExtraerTramoPdfQueryValidator().Validate(Consulta(3, 3)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void El_validador_exige_el_Tenant_del_inicio()
    {
        new ExtraerTramoPdfQueryValidator().Validate(Consulta(1, 1) with { TenantEsperadoId = Guid.Empty }).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task La_extraccion_pide_al_divisor_la_cota_del_tramo_calculada_con_el_original()
    {
        var consulta = Consulta(1, 2);

        await Handler().Handle(consulta, CancellationToken.None);

        _divisor.Cotas.Should().Equal(LimitesDivisionPdf.TamanoMaximoTramo(consulta.ContenidoPdf.Length));
    }

    [Fact]
    public async Task Un_tramo_que_pasa_la_cota_es_el_fallo_de_ese_tramo_y_no_se_entrega()
    {
        _divisor.FalloAlExtraer = Error.Crear(ErroresDivisorPdf.DemasiadoGrande, "El tramo supera el tamaño que se puede preparar.");

        var resultado = await Handler().Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ErroresDivisorPdf.DemasiadoGrande);
    }

    [Fact]
    public async Task Si_el_Tenant_activo_cambia_mientras_se_extrae_el_tramo_no_se_entrega()
    {
        var tenant = new TenantFijo(_tenantId);
        _divisor.AlExtraer = () => tenant.TenantId = _otroTenantId;

        var resultado = await Handler(tenant: tenant).Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado);
        _divisor.Extraidos.Should().Equal([(1, 2)], "el cambio llega con la extracción ya hecha: lo que se corta es la entrega");
    }

    [Fact]
    public async Task Si_se_retira_el_acceso_al_Tenant_mientras_se_extrae_el_tramo_no_se_entrega()
    {
        var tenant = new TenantFijo(_tenantId);
        _divisor.AlExtraer = () => tenant.TenantId = null;

        var resultado = await Handler(tenant: tenant).Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado);
        _divisor.Extraidos.Should().Equal([(1, 2)]);
    }

    [Fact]
    public async Task Si_la_Empresa_deja_de_ser_visible_mientras_se_extrae_el_tramo_no_se_entrega()
    {
        _divisor.AlExtraer = () => _empresas.ListaEmpresas.Remove(_propia);

        var resultado = await Handler().Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
        _divisor.Extraidos.Should().Equal([(1, 2)]);
    }

    [Fact]
    public async Task Si_el_Tenant_activo_cambia_mientras_se_lee_la_Empresa_tras_extraer_el_tramo_no_se_entrega()
    {
        // La carrera de la última espera: el Tenant ya se miró, y cambia mientras se espera la lectura de la Empresa.
        var tenant = new TenantFijo(_tenantId);
        _divisor.AlExtraer = () => _empresas.AlLeer = () => tenant.TenantId = _otroTenantId;

        var resultado = await Handler(tenant: tenant).Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado);
        _divisor.Extraidos.Should().Equal([(1, 2)]);
        _empresas.AlLeer.Should().BeNull("la lectura de la Empresa tras extraer sí ocurrió");
    }

    [Fact]
    public async Task Si_el_Tenant_activo_cambia_mientras_se_lee_la_Empresa_antes_de_extraer_no_se_abre_el_PDF()
    {
        var tenant = new TenantFijo(_tenantId);
        _empresas.AlLeer = () => tenant.TenantId = _otroTenantId;

        var resultado = await Handler(tenant: tenant).Handle(Consulta(1, 2), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado);
        _divisor.Abiertos.Should().Be(0);
    }

    [Theory]
    [InlineData(typeof(EntradaPdfRetenidaException))]
    [InlineData(typeof(PresupuestoMemoriaAgotadoException))]
    public async Task Un_proceso_aparte_sin_turno_o_sin_constar_terminado_no_es_un_fallo_del_tramo_y_llega_a_la_pantalla(Type tipo)
    {
        // La pantalla necesita la excepción: con la entrada retenida no devuelve la reserva del original.
        _divisor.ExcepcionAlExtraer = (Exception)Activator.CreateInstance(tipo)!;

        var extraer = () => Handler().Handle(Consulta(1, 2), CancellationToken.None);

        (await extraer.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(tipo);
    }

    [Fact]
    public void La_reserva_de_la_extraccion_cubre_el_original_y_tres_respuestas_maximas_del_proceso_aparte()
    {
        const long original = 10L * 1024 * 1024;

        // PDFsharp ya no abre el original en este proceso: queda la copia que se le envía y lo que se recibe del proceso
        // aparte, leído en un búfer que crece doblando (viejo y nuevo al crecer; búfer y copia del tramo al final).
        LimitesDivisionPdf.MemoriaExtraccionTramo(original).Should().BeGreaterThanOrEqualTo(
            original + 3 * LimitesDivisionPdf.BytesRespuestaTramo(LimitesDivisionPdf.TamanoMaximoTramo(original)));
        LimitesDivisionPdf.MemoriaCreacionTramos(original).Should().BeGreaterThanOrEqualTo(
            original + 3 * LimitesDivisionPdf.BytesRespuestaTramo(LimitesArchivoSubido.TamanoMaximoBytes));
        LimitesDivisionPdf.TamanoMaximoTramo(original).Should().BeGreaterThan(original, "un tramo puede pasar del original al reescribirse");
    }

    private static void AsignarTenant(EntidadConTenant entidad, Guid tenantId) =>
        typeof(EntidadConTenant).GetProperty(nameof(entidad.TenantId))!.SetValue(entidad, tenantId);

    /// <summary>Hace lo que el filtro global de Tenant hace en EF: solo deja leer las Empresas del Tenant activo.</summary>
    private sealed class EmpresasDelTenant(Guid tenantId) : IEmpresasQueryContext
    {
        private readonly EmpresasQueryContextFalso _todas = new();

        public List<Empresa> ListaEmpresas => _todas.ListaEmpresas;

        /// <summary>Se llama una vez, en la siguiente lectura de Empresas: lo que pasa mientras se espera esa lectura.</summary>
        public Action? AlLeer { get; set; }

        public IQueryable<Empresa> Empresas
        {
            get
            {
                var alLeer = AlLeer;
                AlLeer = null;
                alLeer?.Invoke();
                return new TestAsyncQueryable<Empresa>(_todas.ListaEmpresas.Where(e => e.TenantId == tenantId).AsQueryable());
            }
        }

        public IQueryable<CaeManager.Domain.Empresas.CredencialAccesoEmpresa> CredencialesAccesoEmpresa => _todas.CredencialesAccesoEmpresa;
        public IQueryable<CaeManager.Domain.RelacionesEmpresariales.RelacionEmpresarial> RelacionesEmpresariales => _todas.RelacionesEmpresariales;
        public IQueryable<CaeManager.Domain.Empresas.PlataformaCaeClienteEmpresarial> PlataformasCaeClienteEmpresarial => _todas.PlataformasCaeClienteEmpresarial;
        public IQueryable<CaeManager.Domain.Empresas.AccesoPlataformaCaeEmpresa> AccesosPlataformaCaeEmpresa => _todas.AccesosPlataformaCaeEmpresa;
        public IQueryable<CaeManager.Domain.Empresas.DelegacionEmpresa> DelegacionesEmpresa => _todas.DelegacionesEmpresa;
        public IQueryable<CaeManager.Domain.Empresas.OficioEmpresa> OficiosEmpresa => _todas.OficiosEmpresa;
    }

    /// <summary>El Tenant activo; una prueba lo cambia a mitad de la extracción, como el selector lateral.</summary>
    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; set; } = tenantId;
    }

    private sealed class Disponibilidad(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    private sealed class DivisorFalso : IDivisorPdf
    {
        public List<(int, int)> Extraidos { get; } = [];
        public int Abiertos { get; private set; }
        public int Cerrados { get; private set; }
        public Error? FalloAlAbrir { get; set; }
        public Exception? ExcepcionAlExtraer { get; set; }
        public Error? FalloAlExtraer { get; set; }
        public List<long> Cotas { get; } = [];

        /// <summary>Se llama con el tramo ya extraído, dentro de la extracción.</summary>
        public Action? AlExtraer { get; set; }

        public Task<Result<IPdfParaDividir>> AbrirAsync(byte[] contenidoPdf, CancellationToken cancellationToken)
        {
            Abiertos++;
            return Task.FromResult(FalloAlAbrir is { } fallo ? Result.Fallo<IPdfParaDividir>(fallo) : Result.Exito<IPdfParaDividir>(new Abierto(this)));
        }

        private sealed class Abierto(DivisorFalso divisor) : IPdfParaDividir
        {
            public int NumeroPaginas => 6;
            public bool EstaFirmado => false;

            public Task<Result<byte[]>> ExtraerPaginasAsync(int paginaInicial, int paginaFinal, long tamanoMaximoBytes, CancellationToken cancellationToken)
            {
                divisor.Cotas.Add(tamanoMaximoBytes);
                if (divisor.ExcepcionAlExtraer is { } excepcion)
                    throw excepcion;
                if (divisor.FalloAlExtraer is { } fallo)
                    return Task.FromResult(Result.Fallo<byte[]>(fallo));
                divisor.Extraidos.Add((paginaInicial, paginaFinal));
                divisor.AlExtraer?.Invoke();
                return Task.FromResult(Result.Exito(new[] { (byte)paginaInicial, (byte)paginaFinal }));
            }

            public void Dispose() => divisor.Cerrados++;
        }
    }
}
