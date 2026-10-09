using System.Reflection;
using CaeManager.Domain.Auditoria;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Auditoria;

public class AccionesDeRegistroAuditoriaTests
{
    /// <summary>
    /// <c>RegistrosAuditoria.Accion</c> es <c>varchar(20)</c>
    /// (<c>RegistroAuditoriaConfiguration</c>). Una acción propia más larga compila,
    /// pasa cualquier test con dobles y revienta con 22001 al guardar — en la misma
    /// transacción que el cambio que debía auditar, que entonces tampoco se guarda.
    /// </summary>
    [Fact]
    public void Toda_accion_con_nombre_propio_cabe_en_la_columna()
    {
        var acciones = typeof(RegistroAuditoria)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(c => c.IsLiteral && c.FieldType == typeof(string) && c.Name.StartsWith("Accion", StringComparison.Ordinal))
            .ToDictionary(c => c.Name, c => (string)c.GetRawConstantValue()!);

        acciones.Should().ContainKey(nameof(RegistroAuditoria.AccionActivacionEmitida),
            "control positivo: la reflexión encuentra las constantes de acción");
        acciones.Should().OnlyContain(a => a.Value.Length <= 20);
    }
}
