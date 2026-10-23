using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

public class CaducidadClavesApiTests
{
    [Fact]
    public void La_migracion_da_30_dias_desde_el_despliegue_y_exige_fecha_conservando_el_default()
    {
        // El DEFAULT sobrevive al NOT NULL para que la ranura anterior de un relevo azul/verde, que
        // no conoce la columna, pueda seguir dando de alta claves mientras conviven las dos.
        var operaciones = new AnadeCaducidadClavesApi().UpOperations;
        operaciones.Should().HaveCount(2);
        var alta = operaciones[0].Should().BeOfType<AddColumnOperation>().Subject;
        alta.Table.Should().Be("ClavesApi");
        alta.Name.Should().Be(nameof(ClaveApi.CaducaEnUtc));
        alta.IsNullable.Should().BeTrue();
        alta.DefaultValueSql.Should().Be("now() + interval '30 days'");
        var requerida = operaciones[1].Should().BeOfType<AlterColumnOperation>().Subject;
        requerida.Name.Should().Be(alta.Name);
        requerida.IsNullable.Should().BeFalse();
        requerida.DefaultValueSql.Should().Be(alta.DefaultValueSql);
        requerida.OldColumn.DefaultValueSql.Should().Be(alta.DefaultValueSql);
    }

    [Fact]
    public void El_modelo_exige_caducidad_y_el_motivo_solo_se_guarda_en_la_auditoria()
    {
        using var contexto = CrearContexto();
        var modelo = contexto.Model.FindEntityType(typeof(ClaveApi))!;
        var caducidad = modelo.FindProperty(nameof(ClaveApi.CaducaEnUtc))!;
        caducidad.IsNullable.Should().BeFalse();
        caducidad.GetDefaultValueSql().Should().Be("now() + interval '30 days'",
            "el modelo tiene que coincidir con la migración, que conserva el DEFAULT");
        modelo.FindProperty(nameof(ClaveApi.MotivoEmision)).Should().BeNull();
        var clave = new ClaveApi("ERP Alfa", "prefijo", "hash", Guid.NewGuid(), DateTime.UtcNow.AddDays(30), "Revisión autorizada de Alfa");
        contexto.ClavesApi.Add(clave);
        var metodo = typeof(AuditoriaInterceptor).GetMethod("ConstruirRegistros", BindingFlags.NonPublic | BindingFlags.Static)!;

        var registros = (List<RegistroAuditoria>)metodo.Invoke(null, [contexto, ActorAuditoria.Normal(clave.CreadaPorUsuarioId)])!;

        var registro = registros.Should().ContainSingle().Subject;
        registro.EntidadTipo.Should().Be(nameof(ClaveApi));
        registro.EntidadId.Should().Be(clave.Id);
        registro.ActorRealUsuarioId.Should().Be(clave.CreadaPorUsuarioId);
        registro.DatosDespues.Should().Contain("MotivoEmision").And.Contain("Alfa");
        registro.DatosDespues.Should().Contain(nameof(ClaveApi.CaducaEnUtc));
    }

    private static CaeManagerDbContext CrearContexto() => new(
        new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-modelo;Username=x;Password=x").Options,
        new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });
}
