using System.Text;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Quita los comentarios de un fuente de <c>src</c> para que los trinquetes de texto no
/// se cieguen con uno que nombre lo que buscan (P41c, seguimiento).
///
/// <para>
/// <b>Contrato.</b> Un comentario solo se quita si el recorrido está seguro de que lo es.
/// Donde no lo está, el texto se deja: para un trinquete que cuenta apariciones, quitar de
/// más oculta una superficie (verde con la regla violada) y dejar de más solo obliga a
/// clasificar una mención de más (rojo). Por eso el recorrido de C# distingue de verdad
/// las cadenas (normales, verbatim, interpoladas, crudas con cualquier número de
/// <c>$</c> y de comillas, con huecos <c>{…}</c> que contienen código propio) y los
/// literales de carácter, y el de Razor solo trata como código lo que Razor trata como
/// código.
/// </para>
///
/// <para>
/// <b>Lo que no es.</b> No es un analizador: no resuelve directivas de preprocesador
/// (<c>#if false</c> se recorre como código) ni entiende todo Razor (los bloques de
/// control se delimitan por llaves y paréntesis balanceados, con el marcado de dentro
/// leído como si fuera código). La garantía no descansa en que el recorrido sea perfecto
/// sino en <c>LimpiadorDeComentariosTests</c>, que lo alimenta con el árbol real de
/// <c>src</c> y lo contrasta con Roslyn.
/// </para>
/// </summary>
internal static class LimpiadorDeComentarios
{
    public static string Quitar(string texto, bool razor)
    {
        var recorrido = new Recorrido(texto);
        return razor ? recorrido.Razor() : recorrido.CSharp();
    }

    private sealed class Recorrido(string t)
    {
        private readonly StringBuilder _sb = new(t.Length);
        private int _i;

        private char Sig(int desplazamiento = 1) => _i + desplazamiento < t.Length ? t[_i + desplazamiento] : '\0';

        public string CSharp()
        {
            Codigo(hueco: false);
            return _sb.ToString();
        }

        /// <summary>
        /// Código C#. En un hueco de cadena interpolada termina, sin consumirla, en la
        /// <c>}</c> que lo cierra; el resto termina al acabar el texto.
        /// </summary>
        private void Codigo(bool hueco)
        {
            var llaves = 0;
            var parentesis = 0;

            while (_i < t.Length)
            {
                var c = t[_i];

                if (SaltarComentarioDeCSharp()) continue;
                if (Cadena()) continue;
                if (c == '\'' && LiteralDeCaracter()) continue;

                if (hueco)
                {
                    if (c == '{') llaves++;
                    else if (c == '}')
                    {
                        if (llaves == 0) return;
                        llaves--;
                    }
                    else if (c == '(' || c == '[') parentesis++;
                    else if (c == ')' || c == ']') parentesis--;
                    else if (c == ':' && llaves == 0 && parentesis <= 0 && Sig() != ':' && (_i == 0 || t[_i - 1] != ':'))
                    {
                        // Especificador de formato ({x:yyyy-MM-dd}): texto, no código, hasta la llave de cierre.
                        while (_i < t.Length && t[_i] != '}') _sb.Append(t[_i++]);
                        return;
                    }
                }

                _sb.Append(c);
                _i++;
            }
        }

        private bool SaltarComentarioDeCSharp()
        {
            if (t[_i] != '/') return false;

            if (Sig() == '/')
            {
                while (_i < t.Length && t[_i] != '\n') _i++;
                return true;
            }

            if (Sig() == '*')
            {
                var fin = t.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                _i = fin < 0 ? t.Length : fin + 2;
                return true;
            }

            return false;
        }

        private bool LiteralDeCaracter()
        {
            var largo = _i + 3 < t.Length && t[_i + 1] == '\\' ? t.IndexOf('\'', _i + 3) - _i + 1
                      : _i + 2 < t.Length && t[_i + 2] == '\'' ? 3 : 0;
            if (largo <= 0) return false;

            _sb.Append(t, _i, largo);
            _i += largo;
            return true;
        }

        /// <summary>Si en la posición actual empieza una cadena (con sus prefijos), la copia entera y devuelve true.</summary>
        private bool Cadena()
        {
            var p = _i;
            var dolares = 0;
            var arroba = false;
            while (p < t.Length && (t[p] == '$' || t[p] == '@'))
            {
                if (t[p] == '$') dolares++; else arroba = true;
                p++;
            }

            if (p >= t.Length || t[p] != '"') return false;

            var comillas = 0;
            while (p + comillas < t.Length && t[p + comillas] == '"') comillas++;

            if (comillas >= 3 && !arroba) Cruda(dolares, comillas, p);
            else Normal(interpolada: dolares > 0, verbatim: arroba, p);
            return true;
        }

        /// <summary>Cadena cruda <c>"""…"""</c>, con <paramref name="dolares"/> signos <c>$</c>: los huecos se abren con esa cantidad de llaves.</summary>
        private void Cruda(int dolares, int comillas, int posicionPrimeraComilla)
        {
            var apertura = posicionPrimeraComilla + comillas;
            _sb.Append(t, _i, apertura - _i);
            _i = apertura;

            while (_i < t.Length)
            {
                var c = t[_i];

                if (c == '"')
                {
                    var corrida = 0;
                    while (_i + corrida < t.Length && t[_i + corrida] == '"') corrida++;
                    _sb.Append('"', corrida);
                    _i += corrida;
                    if (corrida >= comillas) return;
                    continue;
                }

                if (dolares > 0 && c == '{')
                {
                    var corrida = 0;
                    while (_i + corrida < t.Length && t[_i + corrida] == '{') corrida++;
                    _sb.Append('{', corrida);
                    _i += corrida;

                    if (corrida < dolares) continue; // menos llaves que signos $: son texto

                    Codigo(hueco: true);
                    var cierre = 0;
                    while (cierre < dolares && _i < t.Length && t[_i] == '}') { _sb.Append('}'); _i++; cierre++; }
                    continue;
                }

                _sb.Append(c);
                _i++;
            }
        }

        private void Normal(bool interpolada, bool verbatim, int posicionComilla)
        {
            _sb.Append(t, _i, posicionComilla + 1 - _i);
            _i = posicionComilla + 1;

            while (_i < t.Length)
            {
                var c = t[_i];

                if (verbatim)
                {
                    if (c == '"')
                    {
                        _sb.Append('"');
                        _i++;
                        if (_i < t.Length && t[_i] == '"') { _sb.Append('"'); _i++; continue; }
                        return;
                    }
                }
                else
                {
                    if (c == '\\')
                    {
                        _sb.Append(c);
                        _i++;
                        if (_i < t.Length) _sb.Append(t[_i++]);
                        continue;
                    }

                    if (c == '"') { _sb.Append(c); _i++; return; }
                    if (c == '\n') return; // una cadena normal sin cerrar acaba con la línea
                }

                if (interpolada && c == '{')
                {
                    if (Sig() == '{') { _sb.Append("{{"); _i += 2; continue; }

                    _sb.Append(c);
                    _i++;
                    Codigo(hueco: true);
                    if (_i < t.Length && t[_i] == '}') { _sb.Append('}'); _i++; }
                    continue;
                }

                if (interpolada && c == '}' && Sig() == '}') { _sb.Append("}}"); _i += 2; continue; }

                _sb.Append(c);
                _i++;
            }
        }

        // ── Razor ────────────────────────────────────────────────────────────

        private static readonly string[] PalabrasDeControl =
            ["if", "foreach", "for", "while", "switch", "lock", "try", "do", "code", "functions"];

        private static readonly string[] Continuaciones = ["else", "catch", "finally", "while"];

        /// <summary>
        /// Razor: el marcado solo tiene dos comentarios, <c>@* … *@</c> y <c>&lt;!-- … --&gt;</c>;
        /// <c>/* */</c> y <c>//</c> no lo son fuera del código. El código (<c>@(…)</c>,
        /// <c>@{…}</c>, <c>@code {…}</c>, <c>@if (…) {…}</c>…) se recorre con el mismo
        /// recorrido de C# —que respeta cadenas— para que un <c>/*</c> o un <c>&lt;!--</c>
        /// dentro de una cadena no abra ningún comentario.
        /// </summary>
        public string Razor()
        {
            while (_i < t.Length)
            {
                var c = t[_i];

                if (c == '@' && Sig() == '*') { SaltarComentarioRazor(); continue; }

                if (c == '<' && string.CompareOrdinal(t, _i, "<!--", 0, 4) == 0)
                {
                    var fin = t.IndexOf("-->", _i + 4, StringComparison.Ordinal);
                    _i = fin < 0 ? t.Length : fin + 3;
                    continue;
                }

                if (c == '@' && Sig() == '@') { _sb.Append("@@"); _i += 2; continue; }

                if (c == '@') { TransicionACodigo(); continue; }

                _sb.Append(c);
                _i++;
            }

            return _sb.ToString();
        }

        private void SaltarComentarioRazor()
        {
            var fin = t.IndexOf("*@", _i + 2, StringComparison.Ordinal);
            _i = fin < 0 ? t.Length : fin + 2;
        }

        private void TransicionACodigo()
        {
            _sb.Append('@');
            _i++;

            if (_i >= t.Length) return;

            if (t[_i] == '(') { Balanceado('(', ')'); return; }
            if (t[_i] == '{') { Balanceado('{', '}'); return; }

            if (!EsInicioDeIdentificador(t[_i])) return;

            var palabra = LeerIdentificador();
            var esControl = Array.IndexOf(PalabrasDeControl, palabra) >= 0;
            // `@using X;` es una directiva; `@using (var x = …) { … }` es un bloque.
            if (palabra == "using" && ProximoNoBlanco() == '(') esControl = true;

            if (esControl)
            {
                SaltarBlancos();
                if (_i < t.Length && t[_i] == '(') { Balanceado('(', ')'); SaltarBlancos(); }
                if (_i < t.Length && t[_i] == '{')
                {
                    Balanceado('{', '}');
                    ContinuacionesDeBloque();
                }
                return;
            }

            // Expresión implícita: `@Foo.Bar(x)[i].Baz`, sin blancos entre las partes.
            while (_i < t.Length)
            {
                if (t[_i] == '(') Balanceado('(', ')');
                else if (t[_i] == '[') Balanceado('[', ']');
                else if (t[_i] == '.' && EsInicioDeIdentificador(Sig())) { _sb.Append('.'); _i++; LeerIdentificador(); }
                else break;
            }
        }

        private void ContinuacionesDeBloque()
        {
            while (true)
            {
                var guardado = (_i, _sb.Length);
                SaltarBlancos();
                if (_i >= t.Length || !EsInicioDeIdentificador(t[_i])) { Deshacer(guardado); return; }

                var palabra = LeerIdentificador();
                if (Array.IndexOf(Continuaciones, palabra) < 0) { Deshacer(guardado); return; }

                SaltarBlancos();
                if (palabra == "else" && string.CompareOrdinal(t, _i, "if", 0, 2) == 0)
                {
                    LeerIdentificador();
                    SaltarBlancos();
                }
                if (_i < t.Length && t[_i] == '(') { Balanceado('(', ')'); SaltarBlancos(); }
                if (_i < t.Length && t[_i] == '{') Balanceado('{', '}');
            }
        }

        private void Deshacer((int Posicion, int Largo) guardado)
        {
            _i = guardado.Posicion;
            _sb.Length = guardado.Largo;
        }

        /// <summary>Copia un bloque delimitado, leyendo su interior como C#, hasta el cierre balanceado.</summary>
        private void Balanceado(char abre, char cierra)
        {
            var profundidad = 0;

            while (_i < t.Length)
            {
                var c = t[_i];

                if (SaltarComentarioDeCSharp()) continue;
                if (c == '@' && Sig() == '*') { SaltarComentarioRazor(); continue; }
                if (Cadena()) continue;
                if (c == '\'' && LiteralDeCaracter()) continue;

                _sb.Append(c);
                _i++;

                if (c == abre) profundidad++;
                else if (c == cierra && --profundidad == 0) return;
            }
        }

        private static bool EsInicioDeIdentificador(char c) => char.IsLetter(c) || c == '_';

        private string LeerIdentificador()
        {
            var inicio = _i;
            while (_i < t.Length && (char.IsLetterOrDigit(t[_i]) || t[_i] == '_')) _i++;
            _sb.Append(t, inicio, _i - inicio);
            return t.Substring(inicio, _i - inicio);
        }

        private void SaltarBlancos()
        {
            while (_i < t.Length && char.IsWhiteSpace(t[_i])) _sb.Append(t[_i++]);
        }

        private char ProximoNoBlanco()
        {
            var p = _i;
            while (p < t.Length && char.IsWhiteSpace(t[p])) p++;
            return p < t.Length ? t[p] : '\0';
        }
    }
}
