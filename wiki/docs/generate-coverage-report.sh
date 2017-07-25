python -m coverxygen --xml-dir ./$1/xml/ --output doc-coverage.info --src-dir ../../Framework/ --scope public,protected --kind enum,varible,function,class,struct,define,file,page
lcov --summary doc-coverage.info
genhtml --no-function-coverage --no-branch-coverage doc-coverage.info -o $1/coverage-report/