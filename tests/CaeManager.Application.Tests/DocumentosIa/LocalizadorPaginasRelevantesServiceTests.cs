using CaeManager.Application.DocumentosIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.DocumentosIa;

public class LocalizadorPaginasRelevantesServiceTests
{
    private readonly LocalizadorPaginasRelevantesService _servicio = new();

    [Fact]
    public void Devuelve_vacio_si_no_hay_paginas()
    {
        var resultado = _servicio.Localizar([]);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public void Selecciona_las_paginas_que_contienen_una_palabra_clave()
    {
        string[] paginas =
        [
            "portada sin datos relevantes",
            "Número de póliza: 12345, tomador: Juan Pérez",
            "texto de relleno sin ninguna palabra clave",
            "cierre del documento",
        ];

        var resultado = _servicio.Localizar(paginas);

        resultado.Should().Contain(1);
    }

    [Fact]
    public void La_busqueda_de_palabras_clave_no_distingue_mayusculas()
    {
        string[] paginas = ["portada", "PÓLIZA número 999", "cierre"];

        var resultado = _servicio.Localizar(paginas);

        resultado.Should().Contain(1);
    }

    [Fact]
    public void Incluye_siempre_la_primera_y_la_ultima_pagina_aunque_no_tengan_palabras_clave()
    {
        string[] paginas = ["logo sin texto", "relleno", "relleno", "relleno", "última hoja sin firma"];

        var resultado = _servicio.Localizar(paginas);

        resultado.Should().Contain(0);
        resultado.Should().Contain(paginas.Length - 1);
    }

    [Fact]
    public void No_selecciona_paginas_sin_ninguna_coincidencia_salvo_la_primera_y_la_ultima()
    {
        string[] paginas = ["primera", "relleno sin nada relevante", "última"];

        var resultado = _servicio.Localizar(paginas);

        resultado.Should().BeEquivalentTo([0, 2]);
    }

    [Fact]
    public void Los_indices_devueltos_estan_en_orden_ascendente_y_sin_duplicados()
    {
        string[] paginas = ["asegurado en portada", "relleno", "prima anual", "vencimiento", "firma final"];

        var resultado = _servicio.Localizar(paginas);

        resultado.Should().BeInAscendingOrder();
        resultado.Should().OnlyHaveUniqueItems();
    }
}
