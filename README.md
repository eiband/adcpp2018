# ADC++ 2018 Code

This is the MSBuild integration of the ADC++ 2018 talk by Daniel Eiband on "Tool-based Refactorings with Visual Studio" which makes it possible to extract a compile_commands.json file from any Visual Studio project.

## Getting Started

In order to build a compile_commands.json file to interop with clang tools create a new build configuration, e.g. "CompilationDB", for the Visual Studio project and import the CompilationDatabase.proj file at the end of the .vcxproj MSBuild project file:

<...

  <!-- Compilation database hook -->
  <Import Condition="'$(Configuration)'=='CompilationDB'" Project="../MSBuild/CompilationDatabase.proj" />
</Project>

When building the specified configuration no compilation is performed. Instead the compile commands are written into the compile_commands.json which can then be used as input to a lot of clang-based tools such as clang-tidy.

## License

This project is licensed under the MIT License - see the [LICENSE.md](LICENSE.md) file for details.
