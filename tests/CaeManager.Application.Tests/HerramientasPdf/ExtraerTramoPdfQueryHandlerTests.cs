using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>
/// La salida «Descargar» de Dividir: extrae un tramo del PDF subido sin crear nada, y solo con Herramientas PDF
/// disponible y la Empresa dentro del alcance de gestión, igual que preparar la división.
/// </summary>
public class ExtraerTramoPdfQueryHandlerTests
{
    private readonly Guid _empresaId = Guid.NewGuid();
    private readonly DivisorFalso _divisor = new();

    private ExtraerTramoPdfQueryHandler Handler(bool disponible = true, AlcanceDatosServiceFalso? alcance = null) =>
        new(new Disponibilidad(disponible), alcance ?? new AlcanceDatosServiceFalso(), _divisor);

    private ExtraerTramoPdfQuery Consulta(int inicial, int final) =>
        new([1, 2, 3], "paquete de Ana.pdf", _empresaId, inicial, final);

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
        var resultado = await Handler(alcance: new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: [_empresaId]))
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

    private sealed class Disponibilidad(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    private sealed class DivisorFalso : IDivisorPdf
    {
        public List<(int, int)> Extraidos { get; } = [];
        public int Abiertos { get; private set; }
        public Error? FalloAlAbrir { get; set; }

        public Result<IPdfParaDividir> Abrir(byte[] contenidoPdf)
        {
            Abiertos++;
            return FalloAlAbrir is { } fallo ? Result.Fallo<IPdfParaDividir>(fallo) : Result.Exito<IPdfParaDividir>(new Abierto(this));
        }

        private sealed class Abierto(DivisorFalso divisor) : IPdfParaDividir
        {
            public int NumeroPaginas => 6;
            public bool EstaFirmado => false;

            public Result<byte[]> ExtraerPaginas(int paginaInicial, int paginaFinal)
            {
                divisor.Extraidos.Add((paginaInicial, paginaFinal));
                return Result.Exito(new[] { (byte)paginaInicial, (byte)paginaFinal });
            }

            public void Dispose()
            {
            }
        }
    }
}
