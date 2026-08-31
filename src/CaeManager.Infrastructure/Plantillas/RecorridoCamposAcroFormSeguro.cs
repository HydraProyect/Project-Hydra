using PdfSharp.Pdf.AcroForms;

namespace CaeManager.Infrastructure.Plantillas;

/// <summary>
/// Recorrido iterativo (con pila explícita, no recursión de C#) de la
/// jerarquía /Kids de un AcroForm, acotado por profundidad y por nodos
/// visitados por referencia. Un PDF hostil puede declarar un ciclo en /Kids
/// (un campo que es, directa o indirectamente, su propio ancestro); recorrer
/// esa estructura con recursión directa agota la pila con un
/// <see cref="StackOverflowException"/> — en .NET no se puede capturar, y
/// tumba el proceso completo, no solo la petición en curso. Mismas cotas y
/// mismo patrón que <c>VerificadorFirmaPdfService.LocalizarFirmas</c>, que
/// recorre el AcroForm crudo (<c>PdfDictionary</c>/<c>PdfArray</c>) en vez del
/// tipado: los dos sitios de este espacio de nombres usan la API tipada de
/// PdfSharp (<see cref="PdfAcroField"/>), así que comparten este recorrido en
/// vez de duplicar las cotas.
///
/// <see cref="PdfAcroField.PdfAcroFieldCollection"/> no resuelve las
/// referencias sin usar el indizador (su <c>GetEnumerator</c> expone los
/// <c>PdfReference</c> crudos del array <c>/Fields</c>), así que el recorrido
/// es siempre por índice.
/// </summary>
internal static class RecorridoCamposAcroFormSeguro
{
    private const int ProfundidadMaxima = 32;
    private const int MaximoCamposVisitados = 5_000;

    public static void Recorrer(PdfAcroField.PdfAcroFieldCollection raiz, Action<PdfAcroField> procesar)
    {
        var visitados = new HashSet<PdfAcroField>(ReferenceEqualityComparer.Instance);
        var pendientes = new Stack<(PdfAcroField.PdfAcroFieldCollection Campos, int Profundidad)>();
        pendientes.Push((raiz, 0));

        while (pendientes.Count > 0)
        {
            var (campos, profundidad) = pendientes.Pop();
            if (profundidad > ProfundidadMaxima) continue;

            for (var i = 0; i < campos.Count; i++)
            {
                if (visitados.Count > MaximoCamposVisitados) return;

                var campo = campos[i];
                if (campo is null || !visitados.Add(campo)) continue; // null o ya visitado (ciclo /Kids)

                procesar(campo);

                if (campo.HasKids)
                    pendientes.Push((campo.Fields, profundidad + 1));
            }
        }
    }
}
