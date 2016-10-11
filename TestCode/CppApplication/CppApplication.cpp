// CppApplication.cpp : Defines the entry point for the console application.
//

#include "pch.h"
#include "main.h"

Cookie::Cookie()
{
  this->_amount = 100;
}

void Cookie::consume(int amount)
{
  this->_amount -= amount;
}

int main()
{
    return 0;
}

