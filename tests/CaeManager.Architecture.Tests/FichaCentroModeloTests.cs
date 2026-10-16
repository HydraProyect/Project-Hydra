using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

public class FichaCentroModeloTests
{
    [Theory]
    [InlineData(nameof(Centro.HorarioAcceso), 500)]
    [InlineData(nameof(Centro.ControlAccesosNombre), 200)]
    [InlineData(nameof(Centro.ControlAccesosTelefono), 50)]
    [InlineData(nameof(Centro.TecnicoPrevencionNombre), 200)]
    [InlineData(nameof(Centro.TecnicoPrevencionTelefono), 50)]
    [InlineData(nameof(Centro.TecnicoPrevencionCorreo), 254)]
    public void Los_campos_de_la_ficha_se_guardan_en_Centros_con_sus_limites(string nombre, int limite)
    {
        using var contexto = CrearContexto();
        var tipo = contexto.Model.FindEntityType(typeof(Centro))!;
        tipo.GetTableName().Should().Be("Centros");
        var propiedad = tipo.FindProperty(nombre)!;
        propiedad.IsNullable.Should().BeTrue();
        propiedad.GetMaxLength().Should().Be(limite);
        tipo.GetDeclaredQueryFilters().Should().NotBeEmpty("la ficha conserva el filtro por Tenant propietario y baja lógica");
        var columna = new AnadeFichaCentro().UpOperations.OfType<AddColumnOperation>().Single(c => c.Name == nombre);
        columna.Table.Should().Be("Centros");
        columna.MaxLength.Should().Be(limite);
        columna.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void La_migracion_solo_anade_columnas_opcionales_sin_modificar_las_politicas_RLS()
    {
        var operaciones = new AnadeFichaCentro().UpOperations;
        operaciones.Should().HaveCount(9).And.AllBeOfType<AddColumnOperation>();
        operaciones.Cast<AddColumnOperation>().Should().OnlyContain(c => c.Table == "Centros" && c.IsNullable && c.DefaultValue == null);
        using var contexto = CrearContexto();
        var tipo = contexto.Model.FindEntityType(typeof(Centro))!;
        foreach (var nombre in new[] { nameof(Centro.TrabajosEnAltura), nameof(Centro.EspaciosConfinados), nameof(Centro.AtmosferasExplosivas) })
        {
            tipo.FindProperty(nombre)!.ClrType.Should().Be(typeof(bool?));
            tipo.FindProperty(nombre)!.IsNullable.Should().BeTrue();
        }
    }

    [Fact]
    public void La_auditoria_no_duplica_los_contactos_personales_al_crearlos_ni_al_vaciarlos()
    {
        using var contexto = CrearContexto();
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de prueba");
        string[] datos = ["Persona accesos", "600111222", "Persona prevención", "600333444", "prl@example.test"];
        centro.ActualizarFicha("7 a 19", datos[0], datos[1], datos[2], datos[3], datos[4], true, null, null);
        contexto.Centros.Add(centro);
        var metodo = typeof(AuditoriaInterceptor).GetMethod("ConstruirRegistros", BindingFlags.NonPublic | BindingFlags.Static)!;
        List<RegistroAuditoria> Registros() => (List<RegistroAuditoria>)metodo.Invoke(null, [contexto, ActorAuditoria.Normal(Guid.NewGuid())])!;
        var alta = Registros().Should().ContainSingle().Subject;
        foreach (var dato in datos) alta.DatosDespues.Should().NotContain(dato);
        alta.DatosDespues.Should().Contain("***").And.Contain("7 a 19");
        contexto.ChangeTracker.AcceptAllChanges();
        centro.ActualizarFicha(null, null, null, null, null, null, null, null, null);
        var vaciado = Registros().Should().ContainSingle().Subject;
        vaciado.Accion.Should().Be("Modificado");
        foreach (var dato in datos) vaciado.DatosAntes.Should().NotContain(dato);
        vaciado.DatosAntes.Should().Contain("***");
    }

    private static CaeManagerDbContext CrearContexto() => new(
        new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x").Options,
        new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });
}
