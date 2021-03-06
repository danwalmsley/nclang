﻿using System;
using NClang;
using Mono.Options;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PInvokeGenerator
{
	class Driver
	{
		public static void Main (string[] args)
		{
			new Driver ().Run (args);
		}

		public static string LibraryName;

		bool InsideUsingDeclaration;
		List<TypeDef> usings = new List<TypeDef> ();

		void Run (string[] args)
		{
			var idx = ClangService.CreateIndex ();
			var tus = new List<ClangTranslationUnit> ();
			TextWriter output = Console.Out;
			foreach (var arg in args) {
				if (arg.StartsWith ("--out:", StringComparison.Ordinal))
					output = File.CreateText (arg.Substring (6));
				else if (arg.StartsWith ("--lib:", StringComparison.Ordinal))
					LibraryName = arg.Substring (6);
				else
					tus.Add (idx.ParseTranslationUnit (arg, null, null, TranslationUnitFlags.None));
			}

			var members = new List<Locatable> ();
			Struct current = null;

			foreach (var tu in tus) {
				Func<ClangCursor,ClangCursor,IntPtr,ChildVisitResult> func = null;
				func = (cursor, parent, clientData) => {
					// FIXME: this doesn't work.
					if (cursor.Kind == CursorKind.InclusionDirective) {
						Console.Error.WriteLine ("Include File " + cursor.IncludedFile);
						idx.ParseTranslationUnit (cursor.IncludedFile.FileName, null, null, TranslationUnitFlags.None).GetCursor ().VisitChildren (func, IntPtr.Zero);
					}
					if (cursor.Kind == CursorKind.TypedefDeclaration) {
						if (cursor.Location.FileLocation.File != null) {
							var alias = ToTypeName (cursor.CursorType);
							InsideUsingDeclaration = true;
							if (usings.All (u => u.Alias != alias)) {
								var actual = ToTypeName (cursor.TypeDefDeclUnderlyingType);
								usings.Add (new TypeDef () { Alias = alias, Actual = actual });
							}
							InsideUsingDeclaration = false;
						}
					}
					if (cursor.Kind == CursorKind.FieldDeclaration)
						current.Fields.Add (new Variable () {
							Type = ToTypeName (cursor.CursorType),
							Name = cursor.Spelling
						});
					if (cursor.Kind == CursorKind.StructDeclaration || cursor.Kind == CursorKind.UnionDeclaration) {
						current = new Struct () {
							Name = cursor.DisplayName,
							Line = cursor.Location.FileLocation.Line,
							Column = cursor.Location.FileLocation.Column,
							IsUnion = cursor.Kind == CursorKind.UnionDeclaration
						};
						if (members.All (m => m.Line != current.Line || m.Column != current.Column)) {
							var dup = members.OfType<Struct> ().Where (m => m.Name == current.Name).ToArray ();
							foreach (var d in dup)
								members.Remove (d);
							members.Add (current);
						}
					}
					if (cursor.Kind == CursorKind.FunctionDeclaration) {
						members.Add (new Function () {
							Name = cursor.Spelling,
							Return = ToTypeName (cursor.ResultType),
							Line = cursor.Location.FileLocation.Line,
							Column = cursor.Location.FileLocation.Column,
							Args = Enumerable.Range (0, cursor.ArgumentCount).Select (i => cursor.GetArgument (i)).Select (a => new Variable () { Name = a.Spelling, Type = ToTypeName (a.CursorType) }).ToArray () });
					}
					return ChildVisitResult.Recurse;
				};
				tu.GetCursor ().VisitChildren (func, IntPtr.Zero);
			}

			output.WriteLine ("using System;");
			output.WriteLine ("using System.Runtime.InteropServices;");
			foreach (var u in usings.Distinct (new KeyComparer ()))
				output.WriteLine ("using {0} = {1};", u.Alias, u.Actual);
			output.WriteLine ();
			foreach (var o in members.OfType<Struct> ()) {
				o.Write (output);
				output.WriteLine ();
			}

			output.WriteLine ("class Marshal");
			output.WriteLine ("{");
			foreach (var o in members.OfType<Function> ()) {
				o.Write (output);
				output.WriteLine ();
			}
			output.WriteLine ("}");
			output.WriteLine ();

			output.WriteLine (@"
public struct Pointer<T>
{
	public IntPtr Handle;
	public static implicit operator IntPtr (Pointer<T> value) { return value.Handle; }
}
public struct ArrayOf<T> {}
public struct ConstArrayOf<T> {}
");
			output.Close ();
		}

		class KeyComparer : IEqualityComparer<TypeDef>
		{
			public bool Equals (TypeDef x, TypeDef y)
			{
				return x.Alias == y.Alias;
			}

			public int GetHashCode (TypeDef obj)
			{
				return obj.Alias.GetHashCode ();
			}
		}

		struct TypeDef
		{
			public string Alias;
			public string Actual;
		}

		abstract class Locatable
		{
			public int Line;
			public int Column;

			public abstract void Write (TextWriter w);
		}

		class Variable
		{
			public string Name;
			public string Type;
		}

		class Struct : Locatable
		{
			public bool IsUnion;
			public string Name;
			public List<Variable> Fields = new List<Variable> ();

			public override void Write (TextWriter w)
			{
				w.WriteLine ("[StructLayout (LayoutKind.Sequential)]");
				w.WriteLine ("struct {0} //line:{1}, column:{2}", Name, Line, Column);
				w.WriteLine ("{");
				foreach (var m in Fields)
					w.WriteLine ("\tpublic {0} {1};", m.Type, m.Name);
				w.WriteLine ("}");
			}
		}

		class Function : Locatable
		{
			public string Name;
			public string Return;
			public Variable [] Args;

			public override void Write (TextWriter w)
			{
				w.WriteLine ("\t// function {0} line:{1}, column:{2}", Name, Line, Column);
				if (Driver.LibraryName != null)
					w.WriteLine ("\t[DllImport (\"{0}\")]", Driver.LibraryName);
				w.WriteLine ("\tinternal static extern {0} {1} ({2});", Return, Name, string.Join (", ", Args.Select (a => a.Type + " " + a.Name)));
			}
		}

		string ToNonKeywordTypeName (string s)
		{
			switch (s) {
			case "byte":
				return "System.Byte";
			case "sbyte":
				return "System.SByte";
			case "short":
				return "System.Int16";
			case "ushort":
				return "System.UInt16";
			case "int":
				return "System.Int32";
			case "uint":
				return "System.UInt32";
			case "long":
				return "System.Int32";
			case "long long":
				return "System.Int64"; // FIXME: this conversion is wrong
			case "ulong":
				return "System.UInt32";
			case "float":
				return "System.Single";
			case "double":
				return "System.Double";
			}
			return s;
		}

		string ToTypeName (ClangType type, bool strip = true)
		{
			var ret = ToTypeName_ (type, strip);
			if (!InsideUsingDeclaration)
				return ret;
			var alias = usings.FirstOrDefault (u => u.Alias == ret).Actual;
			if (alias != null)
				return alias;
			return ToNonKeywordTypeName (ret);
		}

		string ToTypeName_ (ClangType type, bool strip = true)
		{
			if (type.IsPODType) {
				switch (type.Spelling) {
				case "unsigned char":
					return "byte";
				case "char":
					return "byte"; // we most likely don't need sbyte
				case "signed char":
					return "sbyte"; // probably explicit signed specification means something
				case "short":
					return "short";
				case "unsigned short":
					return "ushort";
				case "long":
					return "long"; // FIXME: this should be actually platform dependent
				case "unsigned long":
					return "ulong"; // FIXME: this should be actually platform dependent
				case "int":
					return "int"; // FIXME: this should be actually platform dependent
				case "uint":
					return "uint"; // FIXME: this should be actually platform dependent
				case "float":
					return "float";
				case "double":
					return "double";
				}
				// for aliased types to POD they still have IsPODType = true, so we need to ignore them.
			}
			if (type.Kind == TypeKind.ConstantArray)
				return "ConstArrayOf<" + ToTypeName (type.ElementType) + ">";
			if (type.Kind == TypeKind.IncompleteArray)
				return "ArrayOf<" + ToTypeName (type.ElementType) + ">";
			if (type.Kind == TypeKind.Pointer) {
				if (type.PointeeType != null && type.PointeeType.ArgumentTypeCount >= 0) {
					// function pointer
					var pt = type.PointeeType;
					string ret = ToTypeName (pt.ResultType);
					string f = ret == "void" ? "System.Action<" : "System.Func<";
					for (int i = 0; i < pt.ArgumentTypeCount; i++)
						f += (i > 0 ? ", " : string.Empty) + ToTypeName (pt.GetArgumentType (i));
					if (ret != "void")
						f += (pt.ArgumentTypeCount > 0 ? ", " : string.Empty) + ret;
					f += ">";
					return f;
				} else {
					var t = ToTypeName (type.PointeeType);
					return t == "void" ? "System.IntPtr" : "Pointer<" + t + ">";
				}
			}
			if (strip && type.IsConstQualifiedType)
				return ToTypeName (type, false).Substring (6); // "const "
			else
				return type.Spelling.Replace ("struct ", "").Replace ("union", "");
		}
	}
}
